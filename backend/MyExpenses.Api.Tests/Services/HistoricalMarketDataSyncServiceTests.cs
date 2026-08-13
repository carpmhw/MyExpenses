using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class HistoricalMarketDataSyncServiceTests
{
    /// <summary>驗證同步排程固定使用台灣時區的 23:30。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_UsesTaiwanMarketTimeAt2330()
    {
        var next = HistoricalMarketDataSyncService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 15, 14, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 15, 15, 30, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證週末到達時會跳到下一個平日夜間。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_SkipsWeekend()
    {
        var next = HistoricalMarketDataSyncService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 17, 15, 40, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 20, 15, 30, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證 workflow adapter 先套用 partial result，再重拋原始 SQLite busy 供 runner 分類。</summary>
    [Fact]
    public async Task RunWorkflowAsync_AppliesPartialResultBeforeRethrowingPersistenceException()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new FailingHistoricalStateDbContext(options);
        db.Database.EnsureCreated();
        db.Stocks.Add(new Stock
        {
            Name = "待重試",
            Symbol = "2330",
            Market = StockMarket.Unknown,
            InstrumentType = StockInstrumentType.Stock,
            Shares = 10m,
            BuyPrice = 10m,
            CurrentPrice = 11m,
        });
        await db.SaveChangesAsync();
        var stockId = await db.Stocks.Select(stock => stock.Id).SingleAsync();
        db.FailNextHistoricalFailureStateSave();
        var synchronizer = new HistoricalMarketDataSynchronizer(
            db,
            new TimeoutProvider(),
            catalogService: new FixedCatalogService());
        var aggregation = new ScheduledJobExecutionAccumulator();
        var context = new ScheduledJobWorkflowContext(
            1,
            ScheduledJobKey.HistoricalMarketDataSync,
            new DateTime(2026, 8, 7, 15, 30, 0, DateTimeKind.Utc),
            1,
            aggregation);
        var method = typeof(HistoricalMarketDataSyncService).GetMethod(
            "RunWorkflowAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            null,
            [synchronizer, context, new DateOnly(2026, 8, 7), CancellationToken.None]));
        var exception = await Record.ExceptionAsync(async () => await task);

        var sqlite = Assert.IsType<SqliteException>(exception);
        Assert.Equal(5, sqlite.SqliteErrorCode);
        Assert.True(aggregation.TargetsEnumerated);
        Assert.Equal(["Unknown:2330"], aggregation.FrozenTargetKeys);
        var aggregate = aggregation.BuildAggregate(null);
        Assert.Equal(1, aggregate.TargetCount);
        Assert.Equal(0, aggregate.SucceededCount);
        Assert.Equal(1, aggregate.FailedCount);
        Assert.Equal(1, aggregate.AffectedCount);
        Assert.Equal("DatabaseBusy", aggregate.ResultCode);
        Assert.Equal(StockMarket.Twse, await db.Stocks.Where(stock => stock.Id == stockId)
            .Select(stock => stock.Market).SingleAsync());
    }

    /// <summary>驗證 runner 終態使用完整 pending disposition，不被後續 synthetic failure code 污染。</summary>
    [Fact]
    public async Task RunWorkflowAsync_KeepsDatabaseBusyForAllPendingTargetsInRunner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new FailingHistoricalStateDbContext(options);
        db.Database.EnsureCreated();
        db.Stocks.AddRange(
            CreateStock("第一標的", "1111"),
            CreateStock("第二標的", "2222"));
        await db.SaveChangesAsync();
        db.FailNextHistoricalFailureStateSave();
        var synchronizer = new HistoricalMarketDataSynchronizer(
            db,
            new TimeoutProvider(),
            catalogService: new FixedCatalogService());
        var runner = CreateRunner(db);

        var execution = await runner.RunAsync(
            ScheduledJobKey.HistoricalMarketDataSync,
            new DateTime(2026, 8, 7, 15, 30, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 7),
            (context, token) => InvokeRunWorkflowAsync(
                synchronizer,
                context,
                new DateOnly(2026, 8, 7),
                token));

        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.Equal(1, execution.AttemptCount);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(0, execution.SucceededCount);
        Assert.Equal(2, execution.FailedCount);
        Assert.Equal("DatabaseBusy", execution.ResultCode);
    }

    /// <summary>驗證 runner 取消終態保留同 attempt 已提交的成功 target 與歷史 row。</summary>
    [Fact]
    public async Task RunWorkflowAsync_PreservesCommittedProgressWhenRunnerIsCanceled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        db.Stocks.AddRange(
            CreateStock("第一標的", "1111"),
            CreateStock("第二標的", "2222"));
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var synchronizer = new HistoricalMarketDataSynchronizer(
            db,
            new SuccessThenCancelingProvider(cancellation),
            catalogService: new FixedCatalogService());
        var runner = CreateRunner(db);

        var execution = await runner.RunAsync(
            ScheduledJobKey.HistoricalMarketDataSync,
            new DateTime(2026, 8, 7, 15, 30, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 7),
            (context, token) => InvokeRunWorkflowAsync(
                synchronizer,
                context,
                new DateOnly(2026, 8, 7),
                token),
            cancellation.Token);

        Assert.Equal(ScheduledJobExecutionStatus.Canceled, execution.Status);
        Assert.Equal("Canceled", execution.ResultCode);
        Assert.Equal(1, execution.AttemptCount);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(1, execution.SucceededCount);
        Assert.Equal(1, execution.FailedCount);
        Assert.Equal(1, execution.AffectedCount);
        Assert.Equal(1, await db.HistoricalAdjustedPrices.CountAsync());
    }

    /// <summary>驗證背景服務遇到非 host 取消例外時記錄故障並繼續下一輪，而非直接結束。</summary>
    [Fact]
    public async Task ExecuteAsync_ContinuesAfterNonHostCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var scopes = new CancelingScopeFactory(cancellation);
        var service = new HistoricalMarketDataSyncService(
            scopes,
            NullLogger<HistoricalMarketDataSyncService>.Instance,
            new ImmediateTimeProvider());
        var method = typeof(HistoricalMarketDataSyncService).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(service, [cancellation.Token]));
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, scopes.CallCount);
    }

    /// <summary>建立測試持股。</summary>
    private static Stock CreateStock(string name, string symbol)
        => new()
        {
            Name = name,
            Symbol = symbol,
            Market = StockMarket.Twse,
            InstrumentType = StockInstrumentType.Stock,
            Shares = 10m,
            BuyPrice = 10m,
            CurrentPrice = 11m,
        };

    /// <summary>建立只執行一次 attempt 的測試 runner。</summary>
    private static ScheduledJobRunner CreateRunner(AppDbContext db)
        => new(
            new ScheduledJobExecutionRepository(db),
            NullLogger<ScheduledJobRunner>.Instance,
            options: new ScheduledJobRunnerOptions
            {
                MaxAttempts = 1,
                RetryDelay = TimeSpan.Zero,
            });

    /// <summary>透過既有 private adapter 執行歷史 workflow。</summary>
    private static Task<ScheduledJobWorkflowResult> InvokeRunWorkflowAsync(
        HistoricalMarketDataSynchronizer synchronizer,
        ScheduledJobWorkflowContext context,
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        var method = typeof(HistoricalMarketDataSyncService).GetMethod(
            "RunWorkflowAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task<ScheduledJobWorkflowResult>>(method.Invoke(
            null,
            [synchronizer, context, localDate, cancellationToken]));
    }

    /// <summary>提供固定完整官方市場 catalog。</summary>
    private sealed class FixedCatalogService : IOfficialMarketCatalogService
    {
        private static readonly OfficialMarketCatalogSnapshot Snapshot = new(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)]));

        /// <summary>回傳固定完整雙市場 snapshot。</summary>
        public Task<OfficialMarketCatalogSnapshot> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot);

        /// <summary>以固定 snapshot 解析市場。</summary>
        public Task<OfficialMarketResolution> LookupAsync(
            string? symbol,
            CancellationToken cancellationToken = default)
            => Task.FromResult(OfficialMarketCatalogResolver.Resolve(Snapshot, symbol));
    }

    /// <summary>提供固定 timeout 的歷史行情 provider。</summary>
    private sealed class TimeoutProvider : IHistoricalAdjustedPriceProvider
    {
        /// <summary>拋出可重試的 provider timeout。</summary>
        public Task<HistoricalPriceProviderResult> GetPricesAsync(
            StockMarket market,
            string symbol,
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default)
            => throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
    }

    /// <summary>第一個 request 成功後於第二個 request 取消 host token。</summary>
    private sealed class SuccessThenCancelingProvider : IHistoricalAdjustedPriceProvider
    {
        private readonly CancellationTokenSource _cancellation;
        private int _callCount;

        /// <summary>初始化可控制 host cancellation 的 provider。</summary>
        public SuccessThenCancelingProvider(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        /// <summary>第一次回傳一筆歷史資料，第二次取消並拋出 OperationCanceledException。</summary>
        public Task<HistoricalPriceProviderResult> GetPricesAsync(
            StockMarket market,
            string symbol,
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                return Task.FromResult(new HistoricalPriceProviderResult(
                    "YahooChart",
                    symbol + ".TW",
                    "TAI",
                    "TWD",
                    [new HistoricalPricePoint(new DateOnly(2026, 8, 6), 100m)]));
            }

            _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("取消後不應繼續執行");
        }
    }

    /// <summary>提供可精準注入歷史失敗狀態 SQLite busy 的測試 context。</summary>
    private sealed class FailingHistoricalStateDbContext : AppDbContext
    {
        private bool _failNextHistoricalFailureStateSave;

        /// <summary>初始化使用測試 SQLite options 的 context。</summary>
        public FailingHistoricalStateDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        /// <summary>安排下一次歷史失敗狀態保存拋出 SQLite busy。</summary>
        public void FailNextHistoricalFailureStateSave()
        {
            _failNextHistoricalFailureStateSave = true;
        }

        /// <summary>只攔截 ProviderError 狀態保存，其他交易維持正常。</summary>
        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            if (_failNextHistoricalFailureStateSave
                && ChangeTracker.Entries<HistoricalPriceSyncState>().Any(entry =>
                    entry.State is EntityState.Added or EntityState.Modified
                    && entry.Entity.Status == HistoricalPriceSyncStatus.ProviderError))
            {
                _failNextHistoricalFailureStateSave = false;
                throw new SqliteException("database is locked", 5);
            }

            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    /// <summary>第一輪拋非 host 取消例外，第二輪取消 host token 的 scope factory。</summary>
    private sealed class CancelingScopeFactory : IServiceScopeFactory
    {
        private readonly CancellationTokenSource _cancellation;

        /// <summary>初始化控制背景服務停止時機的 scope factory。</summary>
        public CancelingScopeFactory(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        /// <summary>取得建立 scope 的嘗試次數。</summary>
        public int CallCount { get; private set; }

        /// <summary>第一次拋非 host OCE，第二次取消 host 並拋出對應 OCE。</summary>
        public IServiceScope CreateScope()
        {
            CallCount++;
            if (CallCount == 1)
                throw new OperationCanceledException("內部 timeout");
            _cancellation.Cancel();
            throw new OperationCanceledException(_cancellation.Token);
        }
    }

    /// <summary>提供立即完成延遲的時間來源，讓背景迴圈測試不依賴實際等待。</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        /// <summary>回傳固定 UTC 時間。</summary>
        public override DateTimeOffset GetUtcNow()
            => new(new DateTime(2026, 8, 7, 15, 29, 0, DateTimeKind.Utc));

        /// <summary>建立立即觸發 callback 的 timer。</summary>
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => new ImmediateTimer(callback, state);
    }

    /// <summary>實作一次性立即 callback 的測試 timer。</summary>
    private sealed class ImmediateTimer : ITimer
    {
        /// <summary>排程 timer callback 於 thread pool 立即執行。</summary>
        public ImmediateTimer(TimerCallback callback, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        /// <summary>接受 timer 變更以符合 Task.Delay 所需 contract。</summary>
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        /// <summary>釋放 timer，因 callback 已排程而無額外資源。</summary>
        public void Dispose()
        {
        }

        /// <summary>非同步釋放 timer。</summary>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
