using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class InstallmentIntegrityPreflightTests
{
    /// <summary>Verifies duplicate payment periods are rejected before the unique migration runs.</summary>
    [Fact]
    public async Task ValidateAsync_RejectsDuplicatePeriodsWithoutDeletingRows()
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
            "VALUES (NULL, NULL, 100, 2, 50, 2, '2026-06-01', '2026-06-01 00:00:00', 'Active', 'duplicate')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, IsPaid, DueDate) VALUES (1, 1, 50, 0, '2026-06-20')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, IsPaid, DueDate) VALUES (1, 1, 50, 0, '2026-06-20')");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InstallmentIntegrityPreflight.ValidateAsync(db));

        Assert.Equal(2, await db.InstallmentPayments.CountAsync());
    }
}
