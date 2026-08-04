using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class SingleOwnerIntegrityPreflightTests
{
    /// <summary>驗證 legacy multi-user database 會在 migration 前停止且不修改資料。</summary>
    [Fact]
    public async Task ValidateAsync_RejectsLegacyMultipleUsersWithoutDeletingData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync("20260802090418_AddAtomicFinancialCommands");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Users (Id, Email, PasswordHash, DisplayName, IsTwoFactorEnabled, TokenVersion, CreatedAt, UpdatedAt) " +
            "VALUES (1, 'one@example.com', 'hash', 'One', 0, 1, '2026-08-01 00:00:00', '2026-08-01 00:00:00'), " +
            "(2, 'two@example.com', 'hash', 'Two', 0, 1, '2026-08-01 00:00:00', '2026-08-01 00:00:00')");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SingleOwnerIntegrityPreflight.ValidateAsync(db));

        Assert.Contains("more than one owner", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM Users").SingleAsync());
        Assert.Equal(
            "20260802090418_AddAtomicFinancialCommands",
            await db.Database.SqlQueryRaw<string>(
                "SELECT MigrationId AS Value FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1")
                .SingleAsync());
    }

    /// <summary>驗證 zero-owner 與 one-owner legacy database 可通過 preflight。</summary>
    [Fact]
    public async Task ValidateAsync_AllowsZeroOrOneLegacyUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync("20260802090418_AddAtomicFinancialCommands");

        await SingleOwnerIntegrityPreflight.ValidateAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Users (Id, Email, PasswordHash, DisplayName, IsTwoFactorEnabled, TokenVersion, CreatedAt, UpdatedAt) " +
            "VALUES (1, 'one@example.com', 'hash', 'One', 0, 1, '2026-08-01 00:00:00', '2026-08-01 00:00:00')");

        await SingleOwnerIntegrityPreflight.ValidateAsync(db);
    }

    /// <summary>建立使用已開啟 in-memory connection 的 EF Core SQLite context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        return new AppDbContext(options);
    }
}
