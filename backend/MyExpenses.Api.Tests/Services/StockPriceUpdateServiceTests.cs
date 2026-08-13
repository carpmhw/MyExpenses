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

public class StockPriceUpdateServiceTests
{
    /// <summary>驗證目前股價排程使用台灣時間的 23:00 cutoff。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_UsesTaiwanMarketTime()
    {
        var next = StockPriceUpdateService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 15, 5, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證目前股價排程跳過台灣週末並使用 23:00。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_SkipsTaiwanMarketWeekend()
    {
        var next = StockPriceUpdateService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 17, 15, 40, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 20, 15, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證台灣平日 15:00 至 22:59 不會提前換日。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_BeforeCutoffKeepsSameDay()
    {
        var next = StockPriceUpdateService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 15, 7, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證超過一天的 delay 顯示使用總時數而非 TimeSpan.Hours。</summary>
    [Fact]
    public void FormatDelay_UsesTotalHoursForLongWait()
    {
        Assert.Equal("26h 5m", StockPriceUpdateService.FormatDelay(TimeSpan.FromHours(26) + TimeSpan.FromMinutes(5)));
    }

    /// <summary>驗證背景服務遇到非 host 取消例外時會繼續下一輪，而非直接結束。</summary>
    [Fact]
    public async Task ExecuteAsync_ContinuesAfterNonHostCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var scopes = new CancelingScopeFactory(cancellation);
        var service = new StockPriceUpdateService(
            scopes,
            new EmptyHttpClientFactory(),
            NullLogger<StockPriceUpdateService>.Instance,
            new ImmediateTimeProvider());
        var method = typeof(StockPriceUpdateService).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(service, [cancellation.Token]));
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, scopes.CallCount);
    }

    /// <summary>驗證 adapter 先套用目前價格 partial result，再讓 runner 以 host 取消完成 execution。</summary>
    [Fact]
    public async Task RunWorkflowAsync_PreservesPartialProgressWhenRunnerIsCanceled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var twseStock = CreateStock("1111", StockMarket.Twse);
        var tpexStock = CreateStock("2222", StockMarket.Tpex);
        db.Stocks.AddRange(twseStock, tpexStock);
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider("TWSE", _ =>
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("1111", 100m)])),
            new FakeCurrentPriceProvider("TPEx", _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }));
        var runner = new ScheduledJobRunner(
            new ScheduledJobExecutionRepository(db),
            NullLogger<ScheduledJobRunner>.Instance,
            options: new ScheduledJobRunnerOptions { MaxAttempts = 1, RetryDelay = TimeSpan.Zero });

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 7, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 7),
            (context, token) => InvokeRunWorkflowAsync(workflow, context, token),
            cancellation.Token);

        Assert.Equal(ScheduledJobExecutionStatus.Canceled, execution.Status);
        Assert.Equal("Canceled", execution.ResultCode);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(1, execution.SucceededCount);
        Assert.Equal(1, execution.FailedCount);
        Assert.Equal(1, execution.AffectedCount);
        Assert.Equal(100m, await db.Stocks.Where(stock => stock.Id == twseStock.Id)
            .Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證 raw SQLite busy 重試時保留首次 frozen universe 並保留先前成功 aggregate。</summary>
    [Fact]
    public async Task RunWorkflowAsync_PreservesFrozenTargetsAfterPartialDatabaseBusy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var twseStock = CreateStock("1111", StockMarket.Twse);
        var tpexStock = CreateStock("2222", StockMarket.Tpex);
        db.Stocks.AddRange(twseStock, tpexStock);
        await db.SaveChangesAsync();
        var tpexCalls = 0;
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider("TWSE", _ =>
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("1111", 100m)])),
            new FakeCurrentPriceProvider("TPEx", _ =>
            {
                tpexCalls++;
                if (tpexCalls == 1)
                {
                    db.Stocks.Add(CreateStock("3333", StockMarket.Twse));
                    db.SaveChanges();
                    throw new SqliteException("database is locked", 5);
                }

                return CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("2222", 88m)]);
            }));
        var runner = new ScheduledJobRunner(
            new ScheduledJobExecutionRepository(db),
            NullLogger<ScheduledJobRunner>.Instance,
            options: new ScheduledJobRunnerOptions { MaxAttempts = 2, RetryDelay = TimeSpan.Zero });

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 7, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 7),
            (context, token) => InvokeRunWorkflowAsync(workflow, context, token));

        Assert.Equal(ScheduledJobExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(2, execution.AttemptCount);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(2, execution.SucceededCount);
        Assert.Equal(0, execution.FailedCount);
        Assert.Equal(2, execution.AffectedCount);
        Assert.Equal(0m, await db.Stocks.Where(stock => stock.Symbol == "3333")
            .Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證 non-host OCE partial 經 adapter 後重試且第二 attempt 不會納入新 target。</summary>
    [Fact]
    public async Task RunWorkflowAsync_RetriesNonHostCancellationWithoutFrozenTargetDrift()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var twseStock = CreateStock("1111", StockMarket.Twse);
        var tpexStock = CreateStock("2222", StockMarket.Tpex);
        db.Stocks.AddRange(twseStock, tpexStock);
        await db.SaveChangesAsync();
        var tpexCalls = 0;
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider("TWSE", _ =>
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("1111", 100m)])),
            new FakeCurrentPriceProvider("TPEx", _ =>
            {
                tpexCalls++;
                if (tpexCalls == 1)
                {
                    db.Stocks.Add(CreateStock("3333", StockMarket.Twse));
                    db.SaveChanges();
                    throw new OperationCanceledException("內部 timeout");
                }

                return CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("2222", 88m)]);
            }));
        var runner = new ScheduledJobRunner(
            new ScheduledJobExecutionRepository(db),
            NullLogger<ScheduledJobRunner>.Instance,
            options: new ScheduledJobRunnerOptions { MaxAttempts = 2, RetryDelay = TimeSpan.Zero });

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 7, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 7),
            (context, token) => InvokeRunWorkflowAsync(workflow, context, token));

        Assert.Equal(ScheduledJobExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(2, execution.AttemptCount);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(2, execution.SucceededCount);
        Assert.Equal(0, execution.FailedCount);
        Assert.Equal(2, execution.AffectedCount);
        Assert.Equal(0m, await db.Stocks.Where(stock => stock.Symbol == "3333")
            .Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>透過價格排程服務的 workflow adapter 執行目前價格同步。</summary>
    private static Task<ScheduledJobWorkflowResult> InvokeRunWorkflowAsync(
        CurrentStockPriceWorkflow workflow,
        ScheduledJobWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var method = typeof(StockPriceUpdateService).GetMethod(
            "RunWorkflowAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task<ScheduledJobWorkflowResult>>(method.Invoke(
            null,
            [workflow, context, cancellationToken]));
    }

    /// <summary>建立使用已開啟 SQLite 連線的測試資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>建立固定欄位的測試持股。</summary>
    private static Stock CreateStock(string symbol, StockMarket market)
        => new()
        {
            Name = symbol,
            Symbol = symbol,
            Market = market,
            InstrumentType = StockInstrumentType.Stock,
            Shares = 1m,
            BuyPrice = 10m,
            CurrentPrice = 0m,
        };

    /// <summary>提供可控制結果的目前價格 provider。</summary>
    private sealed class FakeCurrentPriceProvider : ICurrentPriceProvider
    {
        private readonly Func<CancellationToken, CurrentPriceProviderResult> _handler;

        /// <summary>初始化 provider 名稱與測試回應。</summary>
        public FakeCurrentPriceProvider(
            string providerName,
            Func<CancellationToken, CurrentPriceProviderResult> handler)
        {
            ProviderName = providerName;
            _handler = handler;
        }

        /// <summary>取得 provider 安全名稱。</summary>
        public string ProviderName { get; }

        /// <summary>取得測試 provider 對應市場。</summary>
        public StockMarket Market => StockMarket.Unknown;

        /// <summary>回傳測試指定的目前價格結果。</summary>
        public Task<CurrentPriceProviderResult> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_handler(cancellationToken));
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

    /// <summary>提供 constructor 所需但測試不會使用的空白 HttpClient factory。</summary>
    private sealed class EmptyHttpClientFactory : IHttpClientFactory
    {
        /// <summary>建立不應在排程迴圈測試中使用的 HttpClient。</summary>
        public HttpClient CreateClient(string name) => new();
    }
}
