using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class SingleOwnerMigrationTests
{
    private const string OwnerMarker = "myexpenses-owner";

    /// <summary>驗證 fresh database 取得 singleton owner marker constraints。</summary>
    [Fact]
    public async Task Migration_AddsSingletonOwnerMarkerToEmptyDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        await db.Database.MigrateAsync();

        Assert.Contains("InstallationOwnerMarker", await GetColumnNamesAsync(db, "Users"));
        Assert.True(await IndexExistsAsync(db, "IX_Users_InstallationOwnerMarker"));
        var tableSql = await GetTableSqlAsync(db, "Users");
        Assert.Contains("CHECK", tableSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OwnerMarker, tableSql, StringComparison.Ordinal);
    }

    /// <summary>驗證既有 owner 的 identity 與資料在 singleton migration 後保留。</summary>
    [Fact]
    public async Task Migration_PreservesExistingOwnerIdAndData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync("20260802090418_AddAtomicFinancialCommands");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Users (Id, Email, PasswordHash, DisplayName, TotpSecret, IsTwoFactorEnabled, RecoveryCodes, TokenVersion, CreatedAt, UpdatedAt) " +
            "VALUES (42, 'owner@example.com', 'hash', 'Owner', NULL, 0, NULL, 1, '2026-08-01 00:00:00', '2026-08-01 00:00:00')");

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var owner = await db.Users.SingleAsync();
        var marker = await db.Database.SqlQueryRaw<string>(
            "SELECT InstallationOwnerMarker AS Value FROM Users WHERE Id = 42").SingleAsync();

        Assert.Equal(42, owner.Id);
        Assert.Equal("owner@example.com", owner.Email);
        Assert.Equal("Owner", owner.DisplayName);
        Assert.Equal(OwnerMarker, marker);
    }

    /// <summary>驗證直接插入第二位 owner 會被 database constraint 拒絕。</summary>
    [Fact]
    public async Task SingletonOwnerMarker_RejectsSecondUserOutsideRegistrationEndpoint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        db.Users.Add(new User
        {
            Email = "first@example.com",
            PasswordHash = "hash",
            DisplayName = "First",
        });
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            Email = "second@example.com",
            PasswordHash = "hash",
            DisplayName = "Second",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>建立使用已開啟 in-memory connection 的 EF Core SQLite context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>檢查 migrated schema 是否存在指定 SQLite index。</summary>
    private static async Task<bool> IndexExistsAsync(AppDbContext db, string indexName)
        => await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = {0}",
            indexName).SingleAsync() == 1;

    /// <summary>讀取 SQLite column names 供 migration assertion 使用。</summary>
    private static async Task<IReadOnlyList<string>> GetColumnNamesAsync(AppDbContext db, string tableName)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info([{tableName}])";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(1));
        return names;
    }

    /// <summary>讀取 SQLite table definition 供 check-constraint assertion 使用。</summary>
    private static async Task<string> GetTableSqlAsync(AppDbContext db, string tableName)
        => await db.Database.SqlQueryRaw<string>(
            "SELECT sql AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}",
            tableName).SingleAsync();
}
