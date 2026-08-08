using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class DbInitializerTests
{
    /// <summary>驗證已有 Category 但缺少快照設定時仍會獨立補種停用預設值。</summary>
    [Fact]
    public async Task SeedReferenceDataAsync_SeedsMissingAutoSnapshotConfigIndependently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Categories.Add(new Category
        {
            Name = "既有分類",
            Type = CategoryType.Expense,
            Icon = "Tag",
            Color = "#000000",
            SortOrder = 1,
        });
        await db.SaveChangesAsync();

        await DbInitializer.SeedReferenceDataAsync(db);

        var config = await db.AutoSnapshotConfigs.SingleAsync();
        Assert.False(config.IsEnabled);
        Assert.Equal("Daily", config.Frequency);
        Assert.Null(config.DayOfWeek);
        Assert.Null(config.DayOfMonth);
        Assert.Equal("08:00", config.TimeOfDay);
    }

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
