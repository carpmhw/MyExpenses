using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class AutomaticSnapshotWorkflowTests
{
    /// <summary>驗證到期快照 workflow 一次提交快照與 LastRunAt。</summary>
    [Fact]
    public async Task RunAsync_CommitsSnapshotAndScheduleTimestampTogether()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Daily",
            TimeOfDay = "08:00",
        });
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "測試銀行",
            AccountNumber = "123",
            AccountType = "活期",
            Balance = 100m,
        });
        await db.SaveChangesAsync();

        var workflow = new AutomaticSnapshotWorkflow(
            db,
            new FixedTimeProvider(new DateTime(2026, 8, 8, 0, 1, 0, DateTimeKind.Utc)));

        var result = await workflow.RunAsync(
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 8, 8));

        Assert.Equal(ScheduledJobWorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, await db.SnapshotBatches.CountAsync());
        Assert.Equal(
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            (await db.AutoSnapshotConfigs.SingleAsync()).LastRunAt);
    }

    /// <summary>驗證自動快照對所有外幣帳戶只取得一次匯率 snapshot。</summary>
    [Fact]
    public async Task RunAsync_UsesOneExchangeRateSnapshotForMixedCurrencyAccounts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Daily",
            TimeOfDay = "08:00",
        });
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "123",
            AccountType = "活期",
            Balance = 310m,
            CurrencyCode = "USD",
        });
        await db.SaveChangesAsync();
        var exchangeRateService = new CountingExchangeRateService(CreateRates(0.031m, isStale: true));
        var workflow = new AutomaticSnapshotWorkflow(
            db,
            new FixedTimeProvider(new DateTime(2026, 8, 8, 0, 1, 0, DateTimeKind.Utc)),
            exchangeRateService);

        var result = await workflow.RunAsync(
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 8, 8));

        Assert.Equal(ScheduledJobWorkflowOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, exchangeRateService.Calls);
        var snapshot = await db.SnapshotBatches.SingleAsync();
        Assert.Equal(10000m, snapshot.BankDetails.Single().ConvertedBalance);
        Assert.True(snapshot.ExchangeRateIsStale);
    }

    /// <summary>驗證自動快照缺少外幣匯率時 fail closed 且不更新排程時間。</summary>
    [Fact]
    public async Task RunAsync_WhenExchangeRateIsUnavailable_DoesNotPersistPartialSnapshot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Daily",
            TimeOfDay = "08:00",
        });
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "123",
            AccountType = "活期",
            Balance = 310m,
            CurrencyCode = "USD",
        });
        await db.SaveChangesAsync();
        var exchangeRateService = new CountingExchangeRateService(new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m },
            DateTime.UtcNow,
            false));
        var workflow = new AutomaticSnapshotWorkflow(
            db,
            new FixedTimeProvider(new DateTime(2026, 8, 8, 0, 1, 0, DateTimeKind.Utc)),
            exchangeRateService);

        var result = await workflow.RunAsync(
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 8, 8));

        Assert.Equal(ScheduledJobWorkflowOutcome.Failed, result.Outcome);
        Assert.Equal(ScheduledJobRetryClassification.Permanent, result.Retryability);
        Assert.Equal("ExchangeRateUnavailable", result.ResultCode);
        Assert.Equal(0, await db.SnapshotBatches.CountAsync());
        Assert.Null((await db.AutoSnapshotConfigs.SingleAsync()).LastRunAt);
    }

    /// <summary>驗證 transient 匯率失敗允許共用 runner 重試且不保存部分快照。</summary>
    [Fact]
    public async Task RunAsync_TransientExchangeRateFailureIsRetryable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Daily",
            TimeOfDay = "08:00",
        });
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "123",
            AccountType = "活期",
            Balance = 310m,
            CurrencyCode = "USD",
        });
        await db.SaveChangesAsync();
        var exchangeRateService = new ThrowingExchangeRateService(
            new ExchangeRateUnavailableException(
                innerException: new TimeoutException("timeout"),
                isRetryable: true));
        var workflow = new AutomaticSnapshotWorkflow(
            db,
            new FixedTimeProvider(new DateTime(2026, 8, 8, 0, 1, 0, DateTimeKind.Utc)),
            exchangeRateService);

        var result = await workflow.RunAsync(
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 8, 8));

        Assert.Equal(ScheduledJobWorkflowOutcome.Failed, result.Outcome);
        Assert.Equal(ScheduledJobRetryClassification.Retryable, result.Retryability);
        Assert.Equal("ExchangeRateUnavailable", result.ResultCode);
        Assert.Equal(0, await db.SnapshotBatches.CountAsync());
        Assert.Null((await db.AutoSnapshotConfigs.SingleAsync()).LastRunAt);
    }

    /// <summary>驗證 transient 自動快照匯率失敗會讓共用 runner 執行三次 attempt。</summary>
    [Fact]
    public async Task ScheduledRunner_TransientExchangeRateFailureRetriesThreeTimes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Daily",
            TimeOfDay = "08:00",
        });
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "123",
            AccountType = "活期",
            Balance = 310m,
            CurrencyCode = "USD",
        });
        await db.SaveChangesAsync();
        var scheduledForUtc = new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc);
        var scheduledLocalDate = new DateOnly(2026, 8, 8);
        var exchangeRateService = new ThrowingExchangeRateService(
            new ExchangeRateUnavailableException(
                innerException: new TimeoutException("timeout"),
                isRetryable: true));
        var workflow = new AutomaticSnapshotWorkflow(
            db,
            new FixedTimeProvider(new DateTime(2026, 8, 8, 0, 1, 0, DateTimeKind.Utc)),
            exchangeRateService);
        var runner = new ScheduledJobRunner(
            new ScheduledJobExecutionRepository(db),
            NullLogger<ScheduledJobRunner>.Instance,
            TimeProvider.System,
            new ScheduledJobRunnerOptions
            {
                MaxAttempts = 3,
                RetryDelay = TimeSpan.Zero,
            });

        var execution = await runner.RunAsync(
            ScheduledJobKey.AutomaticSnapshot,
            scheduledForUtc,
            "Asia/Taipei",
            scheduledLocalDate,
            (_, cancellationToken) => workflow.RunAsync(
                scheduledForUtc,
                scheduledLocalDate,
                cancellationToken));

        Assert.Equal(3, execution.AttemptCount);
        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.Equal("ExchangeRateUnavailable", execution.ResultCode);
        Assert.Equal(0, await db.SnapshotBatches.CountAsync());
        Assert.Null((await db.AutoSnapshotConfigs.SingleAsync()).LastRunAt);
    }

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>建立測試用的匯率 snapshot。</summary>
    private static ExchangeRateSnapshot CreateRates(decimal usdRate, bool isStale)
        => new(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m, ["USD"] = usdRate },
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            isStale);

    /// <summary>提供測試匯率並記錄 snapshot 取得次數。</summary>
    private sealed class CountingExchangeRateService : IExchangeRateService
    {
        /// <summary>初始化計數型匯率服務。</summary>
        public CountingExchangeRateService(ExchangeRateSnapshot snapshot) => Snapshot = snapshot;

        /// <summary>取得匯率 snapshot 的呼叫次數。</summary>
        public int Calls { get; private set; }

        /// <summary>測試服務回傳的匯率 snapshot。</summary>
        public ExchangeRateSnapshot Snapshot { get; }

        /// <summary>記錄呼叫並回傳測試 snapshot。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Snapshot);
        }

        /// <summary>依測試 snapshot 執行 TWD 換算。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => currencyCode == "TWD"
                ? amount
                : snapshot.Rates.TryGetValue(currencyCode, out var rate) && rate > 0m ? amount / rate : null;

        /// <summary>回傳測試換算的成功狀態。</summary>
        public bool TryConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot, out decimal convertedAmount)
        {
            var result = ConvertToBase(amount, currencyCode, snapshot);
            convertedAmount = result.GetValueOrDefault();
            return result.HasValue;
        }
    }

    /// <summary>提供固定匯率例外的測試服務。</summary>
    private sealed class ThrowingExchangeRateService : IExchangeRateService
    {
        private readonly Exception _exception;

        /// <summary>初始化每次取得 snapshot 時要拋出的例外。</summary>
        public ThrowingExchangeRateService(Exception exception)
        {
            _exception = exception;
        }

        /// <summary>拋出測試指定的匯率例外。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromException<ExchangeRateSnapshot>(_exception);

        /// <summary>此測試服務不執行金額換算。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => throw new NotSupportedException();

        /// <summary>此測試服務不執行金額換算。</summary>
        public bool TryConvertToBase(
            decimal amount,
            string currencyCode,
            ExchangeRateSnapshot snapshot,
            out decimal convertedAmount)
            => throw new NotSupportedException();
    }

    /// <summary>提供固定 UTC 時間供 workflow 測試使用。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        /// <summary>初始化固定 UTC instant。</summary>
        public FixedTimeProvider(DateTime utcNow)
        {
            _utcNow = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        }

        /// <summary>回傳測試指定的 UTC instant。</summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
