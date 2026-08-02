using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class SnapshotNetWorthMigrationTests
{
    /// <summary>Verifies legacy snapshot totals become assets-only values without inferred liabilities.</summary>
    [Fact]
    public async Task Migration_MarksLegacySnapshotsAsAssetsOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync("20260714164707_UnifySystemTimezone");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO SnapshotBatches (Name, SnapshotDate, Notes, TotalNetWorth, TotalBankBalance, TotalStockValue, TotalStockCost, BankDetails, StockDetails) " +
            "VALUES ('legacy', '2026-06-20 12:00:00', NULL, 1234, 1000, 234, 200, '[]', '[]')");

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var snapshot = await db.SnapshotBatches.SingleAsync();

        Assert.Equal(1234m, snapshot.TotalAssets);
        Assert.Null(snapshot.TotalLiabilities);
        Assert.Equal(NetWorthBasis.AssetsOnly, snapshot.NetWorthBasis);

        db.BankAccounts.Add(new BankAccount
        {
            BankName = "新快照銀行",
            AccountNumber = "54321",
            AccountType = "活期",
            Balance = 500m,
        });
        await db.SaveChangesAsync();

        var newSnapshot = await SnapshotEndpoints.CreateSnapshotAsync(db);

        Assert.Equal(NetWorthBasis.AssetsMinusLiabilities, newSnapshot.NetWorthBasis);
        Assert.NotNull(newSnapshot.TotalLiabilities);
    }
}
