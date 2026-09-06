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
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection);
        EfCoreQueryWarningPolicy.Configure(optionsBuilder, isProduction: false);
        var options = optionsBuilder.Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync("20260802051450_AddSnapshotNetWorthBasis");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Installments (TransactionId, CardId, TotalAmount, Periods, PerPeriod, RemainingPeriods, PurchaseDate, CreatedAt, Status, Description) " +
            "VALUES (NULL, NULL, 100, 2, 50, 2, '2026-06-01', '2026-06-01 00:00:00', 'Active', 'duplicate')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Installments (TransactionId, CardId, TotalAmount, Periods, PerPeriod, RemainingPeriods, PurchaseDate, CreatedAt, Status, Description) " +
            "VALUES (NULL, NULL, 200, 3, 66.67, 3, '2026-06-02', '2026-06-02 00:00:00', 'Active', 'second duplicate')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, IsPaid, DueDate) VALUES (2, 2, 66.67, 0, '2026-07-20')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, IsPaid, DueDate) VALUES (1, 1, 50, 0, '2026-06-20')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, IsPaid, DueDate) VALUES (2, 2, 66.67, 0, '2026-07-20')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, IsPaid, DueDate) VALUES (1, 1, 50, 0, '2026-06-20')");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InstallmentIntegrityPreflight.ValidateAsync(db));

        Assert.Contains("Installment 1 contains duplicate payment period 1", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, await db.InstallmentPayments.CountAsync());
    }
}
