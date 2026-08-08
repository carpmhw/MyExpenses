using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class ScheduledJobExecutionMigrationTests
{
    /// <summary>驗證排程 execution migration 建立 bounded 欄位、UTC 欄位與查詢索引。</summary>
    [Fact]
    public async Task Migration_CreatesScheduledExecutionSchemaAndIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        await db.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(db, "ScheduledJobExecutions"));
        Assert.True(await IndexExistsAsync(db, "IX_ScheduledJobExecutions_JobKey_ScheduledForUtc"));
        Assert.True(await IndexExistsAsync(db, "IX_ScheduledJobExecutions_StartedAtUtc"));
        Assert.True(await IndexExistsAsync(db, "IX_ScheduledJobExecutions_JobKey_StartedAtUtc"));
        Assert.True(await IndexExistsAsync(db, "IX_ScheduledJobExecutions_Status_StartedAtUtc"));
        Assert.Equal("TEXT", await GetColumnTypeAsync(db, "ScheduledJobExecutions", "JobKey"));
        Assert.Equal("TEXT", await GetColumnTypeAsync(db, "ScheduledJobExecutions", "Status"));
        Assert.Equal("TEXT", await GetColumnTypeAsync(db, "ScheduledJobExecutions", "ScheduledForUtc"));
        Assert.Equal("TEXT", await GetColumnTypeAsync(db, "ScheduledJobExecutions", "ScheduledLocalDate"));
        Assert.Equal("TEXT", await GetColumnTypeAsync(db, "ScheduledJobExecutions", "SafeMessage"));
    }

    /// <summary>驗證同一工作與 UTC 排程時槽不能建立第二筆 execution。</summary>
    [Fact]
    public async Task Schema_RejectsDuplicateJobAndScheduledSlot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();

        const string sql = "INSERT INTO ScheduledJobExecutions "
            + "(JobKey, ScheduledForUtc, ScheduleTimeZoneId, ScheduledLocalDate, Status, StartedAtUtc, "
            + "AttemptCount, SucceededCount, FailedCount, AffectedCount) "
            + "VALUES ('StockPriceUpdate', '2026-08-08 15:00:00', 'Asia/Taipei', '2026-08-08', 'Running', "
            + "'2026-08-08 15:00:01', 1, 0, 0, 0)";
        await db.Database.ExecuteSqlRawAsync(sql);

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql));
    }

    /// <summary>驗證回退到上一版 migration 會移除 execution schema。</summary>
    [Fact]
    public async Task Migration_DownRemovesScheduledExecutionSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync("20260806165736_AddStockMarketRiskData");

        Assert.False(await TableExistsAsync(db, "ScheduledJobExecutions"));
    }

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>查詢 SQLite 是否存在指定資料表。</summary>
    private static async Task<bool> TableExistsAsync(AppDbContext db, string tableName)
        => await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}",
            tableName).SingleAsync() == 1;

    /// <summary>查詢 SQLite 是否存在指定索引。</summary>
    private static async Task<bool> IndexExistsAsync(AppDbContext db, string indexName)
        => await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = {0}",
            indexName).SingleAsync() == 1;

    /// <summary>讀取 SQLite 欄位型別，供 migration contract 驗證使用。</summary>
    private static async Task<string?> GetColumnTypeAsync(AppDbContext db, string tableName, string columnName)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info([{tableName}])";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == columnName)
                return reader.GetString(2);
        }

        return null;
    }
}
