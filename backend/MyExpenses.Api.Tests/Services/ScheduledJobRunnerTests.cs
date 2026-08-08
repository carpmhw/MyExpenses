using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class ScheduledJobRunnerTests
{
    /// <summary>驗證第一次 workflow 成功時建立完整的 Succeeded execution 摘要。</summary>
    [Fact]
    public async Task RunAsync_CompletesSuccessfulExecutionOnFirstAttempt()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db);
        var scheduled = new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc);

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            scheduled,
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) => Task.FromResult(new ScheduledJobWorkflowResult
            {
                Outcome = ScheduledJobWorkflowOutcome.Succeeded,
                Retryability = ScheduledJobRetryClassification.None,
                TargetsEnumerated = true,
                TargetKeys = ["stock-1", "stock-2"],
                SucceededTargetKeys = ["stock-1", "stock-2"],
                AffectedRowKeys = ["stock-1", "stock-2"],
                ResultCode = "Completed",
                SafeMessage = "已完成",
            }));

        Assert.Equal(ScheduledJobExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(1, execution.AttemptCount);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(2, execution.SucceededCount);
        Assert.Equal(0, execution.FailedCount);
        Assert.Equal(2, execution.AffectedCount);
        Assert.Equal("Completed", execution.ResultCode);
    }

    /// <summary>驗證可重試錯誤在同一 execution 內最多執行三次並使用五分鐘設定。</summary>
    [Fact]
    public async Task RunAsync_RetriesTransientWorkflowWithinSameExecution()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);
        var attempts = 0;

        var execution = await runner.RunAsync(
            ScheduledJobKey.HistoricalMarketDataSync,
            new DateTime(2026, 8, 8, 15, 30, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(new ScheduledJobWorkflowResult
                {
                    Outcome = ScheduledJobWorkflowOutcome.Failed,
                    Retryability = ScheduledJobRetryClassification.Retryable,
                    TargetsEnumerated = true,
                    TargetKeys = ["instrument-1"],
                    FailedTargetCodes = new Dictionary<string, string> { ["instrument-1"] = "ProviderUnavailable" },
                    ResultCode = "ProviderUnavailable",
                    SafeMessage = "provider unavailable",
                });
            });

        Assert.Equal(3, attempts);
        Assert.Equal(3, execution.AttemptCount);
        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.Equal("ProviderUnavailable", execution.ResultCode);
    }

    /// <summary>驗證前次部分成功後重試不會重複計算目標或受影響資料列。</summary>
    [Fact]
    public async Task RunAsync_AggregatesUniqueTargetsAcrossAttempts()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);
        var attempts = 0;

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new ScheduledJobWorkflowResult
                    {
                        Outcome = ScheduledJobWorkflowOutcome.PartiallySucceeded,
                        Retryability = ScheduledJobRetryClassification.Retryable,
                        TargetsEnumerated = true,
                        TargetKeys = ["a", "b"],
                        SucceededTargetKeys = ["a"],
                        FailedTargetCodes = new Dictionary<string, string> { ["b"] = "ProviderUnavailable" },
                        AffectedRowKeys = ["a"],
                        ResultCode = "IncompleteTargets",
                        SafeMessage = "部分完成",
                    }
                    : new ScheduledJobWorkflowResult
                    {
                        Outcome = ScheduledJobWorkflowOutcome.Succeeded,
                        Retryability = ScheduledJobRetryClassification.None,
                        TargetsEnumerated = true,
                        TargetKeys = ["a", "b"],
                        SucceededTargetKeys = ["a", "b"],
                        AffectedRowKeys = ["a", "b"],
                        ResultCode = "Completed",
                        SafeMessage = "已完成",
                    });
            });

        Assert.Equal(ScheduledJobExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(2, execution.SucceededCount);
        Assert.Equal(0, execution.FailedCount);
        Assert.Equal(2, execution.AffectedCount);
    }

    /// <summary>驗證所有 attempt 都無法列舉目標時保留 null target count 並使用 failure code。</summary>
    [Fact]
    public async Task RunAsync_FailsWithoutTargetCountWhenEnumerationNeverSucceeds()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) => Task.FromResult(new ScheduledJobWorkflowResult
            {
                Outcome = ScheduledJobWorkflowOutcome.Failed,
                Retryability = ScheduledJobRetryClassification.Permanent,
                TargetsEnumerated = false,
                ResultCode = "InvalidProviderResponse",
                SafeMessage = "格式錯誤",
            }));

        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.Null(execution.TargetCount);
        Assert.Equal("InvalidProviderResponse", execution.ResultCode);
    }

    /// <summary>驗證 workflow 拋出取消例外時不會重試且 execution 進入 Canceled。</summary>
    [Fact]
    public async Task RunAsync_MarksCanceledWithoutRetry()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);
        var attempts = 0;

        var execution = await runner.RunAsync(
            ScheduledJobKey.AutomaticSnapshot,
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) =>
            {
                attempts++;
                throw new OperationCanceledException();
            });

        Assert.Equal(1, attempts);
        Assert.Equal(ScheduledJobExecutionStatus.Canceled, execution.Status);
        Assert.Equal("Canceled", execution.ResultCode);
    }

    /// <summary>驗證取消後保存 execution 狀態不會提交 workflow 留下的業務追蹤變更。</summary>
    [Fact]
    public async Task RunAsync_CancellationDoesNotCommitTrackedBusinessChanges()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(new Stock
        {
            Name = "測試持股",
            Symbol = "2330",
            Market = StockMarket.Twse,
            InstrumentType = StockInstrumentType.Stock,
        });
        await db.SaveChangesAsync();
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);

        await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) =>
            {
                db.Stocks.Single().CurrentPrice = 123m;
                throw new OperationCanceledException();
            });

        db.ChangeTracker.Clear();
        Assert.Equal(0m, await db.Stocks.Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證前次部分成功後直接取消仍保留 execution aggregate 數量。</summary>
    [Fact]
    public async Task RunAsync_DirectCancellationPreservesPreviousAggregate()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);
        var attempts = 0;

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromResult(new ScheduledJobWorkflowResult
                    {
                        Outcome = ScheduledJobWorkflowOutcome.PartiallySucceeded,
                        Retryability = ScheduledJobRetryClassification.Retryable,
                        TargetsEnumerated = true,
                        TargetKeys = ["a", "b"],
                        SucceededTargetKeys = ["a"],
                        FailedTargetCodes = new Dictionary<string, string> { ["b"] = "ProviderUnavailable" },
                        AffectedRowKeys = ["a"],
                        ResultCode = "IncompleteTargets",
                    });
                }

                throw new OperationCanceledException();
            });

        Assert.Equal(ScheduledJobExecutionStatus.Canceled, execution.Status);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(1, execution.SucceededCount);
        Assert.Equal(1, execution.FailedCount);
        Assert.Equal(1, execution.AffectedCount);
    }

    /// <summary>驗證不論外部設定為何，同一 execution 最多只執行三次 attempt。</summary>
    [Fact]
    public async Task RunAsync_CapsAttemptsAtThree()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero, maxAttempts: 10);
        var attempts = 0;

        var execution = await runner.RunAsync(
            ScheduledJobKey.HistoricalMarketDataSync,
            new DateTime(2026, 8, 8, 15, 30, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(new ScheduledJobWorkflowResult
                {
                    Outcome = ScheduledJobWorkflowOutcome.Failed,
                    Retryability = ScheduledJobRetryClassification.Retryable,
                    TargetsEnumerated = false,
                    ResultCode = "ProviderUnavailable",
                });
            });

        Assert.Equal(3, attempts);
        Assert.Equal(3, execution.AttemptCount);
    }

    /// <summary>驗證 execution 終態與 fallback 查詢都失敗時仍回傳保守 Running 摘要。</summary>
    [Fact]
    public async Task RunAsync_ReturnsRunningWhenCompletionPersistenceAndFallbackFail()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) =>
            {
                db.Dispose();
                return Task.FromResult(ScheduledJobWorkflowResult.NoWork());
            });

        Assert.Equal(ScheduledJobExecutionStatus.Running, execution.Status);
        Assert.Equal("ExecutionPersistenceFailed", execution.ResultCode);
    }

    /// <summary>建立使用測試 retry delay 的 runner。</summary>
    private static ScheduledJobRunner CreateRunner(
        AppDbContext db,
        TimeSpan? retryDelay = null,
        int maxAttempts = 3)
        => new(
            new ScheduledJobExecutionRepository(db),
            NullLogger<ScheduledJobRunner>.Instance,
            TimeProvider.System,
            new ScheduledJobRunnerOptions
            {
                MaxAttempts = maxAttempts,
                RetryDelay = retryDelay ?? TimeSpan.FromMinutes(5),
            });

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>開啟供測試 context 使用的記憶體 SQLite 連線。</summary>
    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }
}
