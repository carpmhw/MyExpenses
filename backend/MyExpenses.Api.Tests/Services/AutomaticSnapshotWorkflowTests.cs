using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
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
