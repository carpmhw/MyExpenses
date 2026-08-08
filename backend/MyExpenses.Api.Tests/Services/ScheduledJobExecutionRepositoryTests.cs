using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class ScheduledJobExecutionRepositoryTests
{
    /// <summary>驗證 execution 查詢以開始時間與 ID 進行穩定降冪排序。</summary>
    [Fact]
    public async Task QueryAsync_UsesStableDescendingOrder()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var repository = new ScheduledJobExecutionRepository(db);
        var scheduled = new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc);

        await repository.CreateRunningAsync(
            ScheduledJobKey.StockPriceUpdate,
            scheduled.AddMinutes(-2),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            scheduled.AddMinutes(-2));
        await repository.CreateRunningAsync(
            ScheduledJobKey.HistoricalMarketDataSync,
            scheduled.AddMinutes(-1),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            scheduled.AddMinutes(-1));

        var items = await repository.QueryAsync();

        Assert.Equal(2, items.Count);
        Assert.True(items[0].StartedAtUtc > items[1].StartedAtUtc);
    }

    /// <summary>驗證相同排程時槽只會回傳同一筆 execution。</summary>
    [Fact]
    public async Task CreateRunningAsync_ReturnsExistingExecutionForDuplicateSlot()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var repository = new ScheduledJobExecutionRepository(db);
        var scheduled = new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc);

        var first = await repository.CreateRunningAsync(
            ScheduledJobKey.StockPriceUpdate,
            scheduled,
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            scheduled.AddSeconds(1));
        var second = await repository.CreateRunningAsync(
            ScheduledJobKey.StockPriceUpdate,
            scheduled,
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            scheduled.AddSeconds(2));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.ScheduledJobExecutions.CountAsync());
    }

    /// <summary>驗證安全訊息會移除空白並截斷至資料庫契約上限。</summary>
    [Fact]
    public async Task CreateRunningAsync_TruncatesSafeMessageToFiveHundredCharacters()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var repository = new ScheduledJobExecutionRepository(db);
        var message = "  " + new string('x', 600) + "  ";

        var execution = await repository.CreateRunningAsync(
            ScheduledJobKey.AutomaticSnapshot,
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            new DateTime(2026, 8, 8, 0, 0, 1, DateTimeKind.Utc),
            safeMessage: message);

        Assert.NotNull(execution.SafeMessage);
        Assert.Equal(500, execution.SafeMessage!.Length);
        Assert.DoesNotContain(' ', execution.SafeMessage);
    }

    /// <summary>驗證 execution 查詢不會回傳 context 中尚未持久化的終態變更。</summary>
    [Fact]
    public async Task GetByIdAsync_ReturnsPersistedStateOnly()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var repository = new ScheduledJobExecutionRepository(db);
        var execution = await repository.CreateRunningAsync(
            ScheduledJobKey.StockPriceUpdate,
            new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Taipei",
            new DateOnly(2026, 8, 8),
            new DateTime(2026, 8, 8, 15, 0, 1, DateTimeKind.Utc));

        execution.Status = ScheduledJobExecutionStatus.Succeeded;

        var persisted = await repository.GetByIdAsync(execution.Id);

        Assert.NotNull(persisted);
        Assert.Equal(ScheduledJobExecutionStatus.Running, persisted!.Status);
    }

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
