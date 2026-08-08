using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class ScheduledJobRunnerLoggingTests
{
    /// <summary>驗證 runner scope 含 execution 關聯欄位且摘要不保存敏感 provider 內容。</summary>
    [Fact]
    public async Task RunAsync_UsesStructuredScopeAndSafeAggregateMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var logger = new CaptureLogger<ScheduledJobRunner>();
        var runner = new ScheduledJobRunner(
            new ScheduledJobExecutionRepository(db),
            logger,
            TimeProvider.System,
            new ScheduledJobRunnerOptions { RetryDelay = TimeSpan.Zero });

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) => Task.FromResult(new ScheduledJobWorkflowResult
            {
                Outcome = ScheduledJobWorkflowOutcome.Failed,
                Retryability = ScheduledJobRetryClassification.Permanent,
                TargetsEnumerated = true,
                TargetKeys = ["holding-id-17"],
                FailedTargetCodes = new Dictionary<string, string> { ["holding-id-17"] = "ProviderRejected" },
                ResultCode = "ProviderRejected",
                SafeMessage = "https://provider.example.test/quotes?token=secret payload=raw-body",
            }));

        var scope = logger.Scopes.First(item => item.ContainsKey("Attempt"));
        Assert.Equal("StockPriceUpdate", scope["JobKey"]);
        Assert.Equal(execution.Id, scope["ExecutionId"]);
        Assert.Equal(new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc), scope["ScheduledForUtc"]);
        Assert.Equal(1, scope["Attempt"]);
        Assert.DoesNotContain("provider.example.test", execution.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("holding-id-17", execution.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", string.Join(" ", logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-body", string.Join(" ", logger.Messages), StringComparison.Ordinal);
    }

    /// <summary>驗證取消終態也會寫入帶有 execution 關聯欄位的完成 log。</summary>
    [Fact]
    public async Task RunAsync_CancellationLogsTerminalExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var logger = new CaptureLogger<ScheduledJobRunner>();
        var runner = new ScheduledJobRunner(
            new ScheduledJobExecutionRepository(db),
            logger,
            TimeProvider.System,
            new ScheduledJobRunnerOptions { RetryDelay = TimeSpan.Zero });

        var execution = await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            (_, _) => throw new OperationCanceledException());

        Assert.Equal(ScheduledJobExecutionStatus.Canceled, execution.Status);
        Assert.Contains(logger.Scopes, item =>
            item.TryGetValue("ExecutionId", out var value) && Equals(value, execution.Id));
        var scope = logger.Scopes.Last();
        Assert.Equal("StockPriceUpdate", scope["JobKey"]);
        Assert.Contains("canceled", string.Join(" ", logger.Messages), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>保存 logger scope 與 message template 渲染結果的測試 logger。</summary>
    private sealed class CaptureLogger<T> : ILogger<T>
    {
        /// <summary>取得 runner 建立的結構化 scope 欄位。</summary>
        public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];

        /// <summary>取得 logger 寫入的安全訊息。</summary>
        public List<string> Messages { get; } = [];

        /// <summary>允許測試 logger 接收所有層級。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <summary>保存字典型 structured scope。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                Scopes.Add(pairs.ToDictionary(pair => pair.Key, pair => pair.Value));
            return NoopDisposable.Instance;
        }

        /// <summary>保存不包含原始 provider 內容的 log message。</summary>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        /// <summary>提供不做任何動作的 scope disposable。</summary>
        private sealed class NoopDisposable : IDisposable
        {
            /// <summary>取得共用 no-op disposable。</summary>
            public static NoopDisposable Instance { get; } = new();

            /// <summary>結束測試 scope。</summary>
            public void Dispose()
            {
            }
        }
    }
}
