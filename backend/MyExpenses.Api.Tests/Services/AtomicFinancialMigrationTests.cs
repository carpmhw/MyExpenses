using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class AtomicFinancialMigrationTests
{
    /// <summary>Verifies the atomic-command schema adds idempotency storage and schedule uniqueness.</summary>
    [Fact]
    public async Task Migration_AddsIdempotencyRecordsAndUniquePaymentPeriodIndex()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(db, "IdempotencyRecords"));
        Assert.True(await IndexExistsAsync(db, "IX_InstallmentPayments_InstallmentId_Period"));
        Assert.DoesNotContain(
            await GetColumnNamesAsync(db, "Installments"),
            name => name is "RemainingPeriods" or "Status");
    }

    /// <summary>Verifies existing payment rows retain the derived lifecycle state after column removal.</summary>
    [Fact]
    public async Task Migration_PreservesPaymentRowsForDerivedState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync("20260802051450_AddSnapshotNetWorthBasis");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Installments (TransactionId, CardId, TotalAmount, Periods, PerPeriod, RemainingPeriods, PurchaseDate, CreatedAt, Status, Description) " +
            "VALUES (NULL, NULL, 100, 2, 50, 1, '2026-06-01', '2026-06-01 00:00:00', 'Active', 'derived')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, IsPaid, DueDate) VALUES (1, 1, 50, 1, '2026-06-20')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, IsPaid, DueDate) VALUES (1, 2, 50, 0, '2026-07-20')");

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var installment = await db.Installments
            .Include(item => item.Payments)
            .SingleAsync();

        Assert.Equal(1, installment.RemainingPeriods);
        Assert.Equal(MyExpenses.Api.Models.InstallmentStatus.Active, installment.Status);
    }

    /// <summary>Verifies the database rejects duplicate payment periods for one installment.</summary>
    [Fact]
    public async Task UniquePaymentPeriodIndex_RejectsDuplicatePeriod()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();

        var installment = new MyExpenses.Api.Models.Installment
        {
            TotalAmount = 100m,
            Periods = 2,
            PerPeriod = 50m,
            PurchaseDate = new DateOnly(2026, 6, 1),
        };
        db.Installments.Add(installment);
        await db.SaveChangesAsync();
        db.InstallmentPayments.AddRange(
            new MyExpenses.Api.Models.InstallmentPayment
            {
                InstallmentId = installment.Id,
                Period = 1,
                Amount = 50m,
            },
            new MyExpenses.Api.Models.InstallmentPayment
            {
                InstallmentId = installment.Id,
                Period = 1,
                Amount = 50m,
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Verifies rolling back the atomic-command migration restores the previous schema shape.</summary>
    [Fact]
    public async Task Migration_DownRestoresLegacyDerivedColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync("20260802051450_AddSnapshotNetWorthBasis");

        Assert.False(await TableExistsAsync(db, "IdempotencyRecords"));
        Assert.False(await IndexExistsAsync(db, "IX_InstallmentPayments_InstallmentId_Period"));
        Assert.Contains("RemainingPeriods", await GetColumnNamesAsync(db, "Installments"));
        Assert.Contains("Status", await GetColumnNamesAsync(db, "Installments"));
    }

    /// <summary>Checks whether a SQLite table exists in the migrated schema.</summary>
    private static async Task<bool> TableExistsAsync(AppDbContext db, string tableName)
        => await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}",
            tableName).SingleAsync() == 1;

    /// <summary>Checks whether a SQLite index exists in the migrated schema.</summary>
    private static async Task<bool> IndexExistsAsync(AppDbContext db, string indexName)
        => await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = {0}",
            indexName).SingleAsync() == 1;

    /// <summary>Reads SQLite column names for migration assertions.</summary>
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
}
