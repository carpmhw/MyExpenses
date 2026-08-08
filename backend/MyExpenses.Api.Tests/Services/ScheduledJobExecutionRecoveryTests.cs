using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class ScheduledJobExecutionRecoveryTests
{
    /// <summary>驗證啟動復原只中斷 Running execution 並保存固定安全結果。</summary>
    [Fact]
    public async Task RecoverAsync_MarksRunningExecutionsInterrupted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var repository = new ScheduledJobExecutionRepository(db);
        var scheduled = new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc);
        await repository.CreateRunningAsync(
            ScheduledJobKey.StockPriceUpdate,
            scheduled,
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            scheduled.AddSeconds(1));
        var completed = await repository.CreateRunningAsync(
            ScheduledJobKey.HistoricalMarketDataSync,
            scheduled.AddMinutes(30),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            scheduled.AddMinutes(30));
        await repository.CompleteAsync(
            completed.Id,
            ScheduledJobExecutionStatus.Succeeded,
            scheduled.AddMinutes(31),
            0,
            0,
            0,
            0,
            "NoEligibleTargets",
            "沒有需要處理的目標");

        var recovery = new ScheduledJobExecutionRecoveryService(
            repository,
            new FixedTimeProvider(new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(1, await recovery.RecoverAsync());

        var executions = await db.ScheduledJobExecutions.OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(ScheduledJobExecutionStatus.Interrupted, executions[0].Status);
        Assert.Equal(new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc), executions[0].CompletedAtUtc);
        Assert.Equal("InterruptedByRestart", executions[0].ResultCode);
        Assert.Equal("服務重新啟動時中斷執行", executions[0].SafeMessage);
        Assert.Equal(ScheduledJobExecutionStatus.Succeeded, executions[1].Status);
    }

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>提供固定 UTC 時間供啟動復原測試使用。</summary>
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
