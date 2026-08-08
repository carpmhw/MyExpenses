using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class ScheduledJobExecutionRetentionTests
{
    /// <summary>驗證 retention 只刪除嚴格早於 cutoff 的終止 execution。</summary>
    [Fact]
    public async Task CleanupCompletedAsync_DeletesOnlyStrictlyOlderTerminalRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var repository = new ScheduledJobExecutionRepository(db);
        var cutoff = new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc);

        var old = await CreateExecutionAsync(repository, ScheduledJobKey.AutomaticSnapshot, 1);
        var exact = await CreateExecutionAsync(repository, ScheduledJobKey.StockPriceUpdate, 2);
        var nullCompletion = await CreateExecutionAsync(repository, ScheduledJobKey.HistoricalMarketDataSync, 3);
        var running = await CreateExecutionAsync(repository, ScheduledJobKey.AutomaticSnapshot, 4);
        await repository.CompleteAsync(
            old.Id, ScheduledJobExecutionStatus.Succeeded, cutoff.AddTicks(-1), 0, 0, 0, 0, "Completed", "完成");
        await repository.CompleteAsync(
            exact.Id, ScheduledJobExecutionStatus.Succeeded, cutoff, 0, 0, 0, 0, "Completed", "完成");
        nullCompletion.Status = ScheduledJobExecutionStatus.Failed;
        await db.SaveChangesAsync();

        Assert.Equal(1, await repository.CleanupCompletedAsync(cutoff));

        var remaining = await db.ScheduledJobExecutions.OrderBy(item => item.Id).ToListAsync();
        Assert.DoesNotContain(remaining, item => item.Id == old.Id);
        Assert.Contains(remaining, item => item.Id == exact.Id);
        Assert.Contains(remaining, item => item.Id == nullCompletion.Id);
        Assert.Contains(remaining, item => item.Id == running.Id);
    }

    /// <summary>建立指定開始時間的 Running execution。</summary>
    private static Task<ScheduledJobExecution> CreateExecutionAsync(
        ScheduledJobExecutionRepository repository,
        ScheduledJobKey jobKey,
        int minute)
        => repository.CreateRunningAsync(
            jobKey,
            new DateTime(2026, 8, 1, 0, minute, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 1),
            new DateTime(2026, 8, 1, 0, minute, 1, DateTimeKind.Utc));

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
