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

    /// <summary>驗證單一 frozen target 以最後一次失敗 disposition 決定終態代碼。</summary>
    [Fact]
    public async Task RunAsync_UsesLatestFailureCodeForSingleFrozenTarget()
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
                var code = attempts == 1 ? "ProviderUnavailable" : "TargetChanged";
                return Task.FromResult(new ScheduledJobWorkflowResult
                {
                    Outcome = ScheduledJobWorkflowOutcome.Failed,
                    Retryability = attempts == 1
                        ? ScheduledJobRetryClassification.Retryable
                        : ScheduledJobRetryClassification.Permanent,
                    TargetsEnumerated = true,
                    TargetKeys = ["instrument-1"],
                    FailedTargetCodes = new Dictionary<string, string> { ["instrument-1"] = code },
                    ResultCode = code,
                });
            });

        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.Equal(2, execution.AttemptCount);
        Assert.Equal("TargetChanged", execution.ResultCode);
    }

    /// <summary>驗證多個 frozen target 的最後失敗代碼不同時回傳 MultipleFailures。</summary>
    [Fact]
    public async Task RunAsync_ReturnsMultipleFailuresForDifferentFinalTargetCodes()
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
                    Retryability = attempts == 1
                        ? ScheduledJobRetryClassification.Retryable
                        : ScheduledJobRetryClassification.Permanent,
                    TargetsEnumerated = true,
                    TargetKeys = ["instrument-1", "instrument-2"],
                    FailedTargetCodes = attempts == 1
                        ? new Dictionary<string, string>
                        {
                            ["instrument-1"] = "ProviderUnavailable",
                            ["instrument-2"] = "ProviderUnavailable",
                        }
                        : new Dictionary<string, string>
                        {
                            ["instrument-1"] = "TargetChanged",
                            ["instrument-2"] = "ProviderRejected",
                        },
                    ResultCode = attempts == 1 ? "ProviderUnavailable" : "MultipleFailures",
                });
            });

        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.Equal(2, execution.AttemptCount);
        Assert.Equal("MultipleFailures", execution.ResultCode);
    }

    /// <summary>驗證從未成功列舉目標時仍依跨 attempt 歷史失敗代碼決定終態。</summary>
    [Fact]
    public async Task RunAsync_PreservesHistoricalFailureCodesBeforeTargetEnumeration()
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
                    Retryability = attempts == 1
                        ? ScheduledJobRetryClassification.Retryable
                        : ScheduledJobRetryClassification.Permanent,
                    TargetsEnumerated = false,
                    ResultCode = attempts == 1 ? "ProviderUnavailable" : "ProviderRejected",
                });
            });

        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.Equal(2, execution.AttemptCount);
        Assert.Null(execution.TargetCount);
        Assert.Equal("MultipleFailures", execution.ResultCode);
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

    /// <summary>驗證 host token 取消時 workflow 拋出取消例外不會重試且 execution 進入 Canceled。</summary>
    [Fact]
    public async Task RunAsync_MarksCanceledWithoutRetry()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();

        var execution = await runner.RunAsync(
            ScheduledJobKey.AutomaticSnapshot,
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, token) =>
            {
                attempts++;
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            },
            cancellation.Token);

        Assert.Equal(1, attempts);
        Assert.Equal(ScheduledJobExecutionStatus.Canceled, execution.Status);
        Assert.Equal("Canceled", execution.ResultCode);
    }

    /// <summary>驗證未取消 host token 的取消例外視為 timeout 並於同一 execution 重試。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_RetriesNonHostCancellationAsTransientFailure(bool taskCanceled)
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
                throw taskCanceled
                    ? new TaskCanceledException("內部 timeout")
                    : new OperationCanceledException("內部 timeout");
            });

        Assert.Equal(3, attempts);
        Assert.Equal(3, execution.AttemptCount);
        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.NotEqual("Canceled", execution.ResultCode);
        Assert.Equal("TransientFailure", execution.ResultCode);
    }

    /// <summary>驗證 retry delay 的非 host 取消例外不會將 execution 錯標為 Canceled。</summary>
    [Fact]
    public async Task RunAsync_ClassifiesNonHostRetryDelayCancellationAsTransientFailure()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = new ScheduledJobRunner(
            new ScheduledJobExecutionRepository(db),
            NullLogger<ScheduledJobRunner>.Instance,
            new CancelingDelayTimeProvider(),
            new ScheduledJobRunnerOptions
            {
                MaxAttempts = 3,
                RetryDelay = TimeSpan.FromMinutes(1),
            });
        var attempts = 0;

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
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
        Assert.Equal(ScheduledJobExecutionStatus.Failed, execution.Status);
        Assert.NotEqual("Canceled", execution.ResultCode);
        Assert.Equal("MultipleFailures", execution.ResultCode);
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
        using var cancellation = new CancellationTokenSource();

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, token) =>
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

                cancellation.Cancel();
                throw new OperationCanceledException(token);
            },
            cancellation.Token);

        Assert.Equal(ScheduledJobExecutionStatus.Canceled, execution.Status);
        Assert.Equal(2, execution.TargetCount);
        Assert.Equal(1, execution.SucceededCount);
        Assert.Equal(1, execution.FailedCount);
        Assert.Equal(1, execution.AffectedCount);
    }

    /// <summary>驗證 frozen target keys 依首次列舉輸入順序提供給後續 workflow。</summary>
    [Fact]
    public async Task RunAsync_PreservesFrozenTargetInsertionOrder()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var runner = CreateRunner(db, retryDelay: TimeSpan.Zero);
        var attempts = 0;
        IReadOnlyCollection<string>? frozenTargets = null;

        await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (context, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromResult(new ScheduledJobWorkflowResult
                    {
                        Outcome = ScheduledJobWorkflowOutcome.Failed,
                        Retryability = ScheduledJobRetryClassification.Retryable,
                        TargetsEnumerated = true,
                        TargetKeys = ["target-b", "target-a", "target-c"],
                        FailedTargetCodes = new Dictionary<string, string>
                        {
                            ["target-b"] = "ProviderUnavailable",
                            ["target-a"] = "ProviderUnavailable",
                            ["target-c"] = "ProviderUnavailable",
                        },
                        ResultCode = "ProviderUnavailable",
                    });
                }

                frozenTargets = context.FrozenTargetKeys;
                return Task.FromResult(new ScheduledJobWorkflowResult
                {
                    Outcome = ScheduledJobWorkflowOutcome.Failed,
                    Retryability = ScheduledJobRetryClassification.Permanent,
                    TargetsEnumerated = true,
                    TargetKeys = ["target-b", "target-a", "target-c"],
                    FailedTargetCodes = new Dictionary<string, string>
                    {
                        ["target-b"] = "TargetChanged",
                        ["target-a"] = "TargetChanged",
                        ["target-c"] = "TargetChanged",
                    },
                    ResultCode = "TargetChanged",
                });
            });

        Assert.Equal(["target-b", "target-a", "target-c"], frozenTargets);
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

    /// <summary>在 retry delay 建立 timer 時拋出未綁定 host token 的取消例外。</summary>
    private sealed class CancelingDelayTimeProvider : TimeProvider
    {
        /// <summary>回傳固定 UTC 時間供 runner 使用。</summary>
        public override DateTimeOffset GetUtcNow()
            => new(new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc));

        /// <summary>模擬內部 timer timeout 導致的非 host 取消例外。</summary>
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => throw new OperationCanceledException("內部 timer timeout");
    }
}
