using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class InstallmentPaidDateMigrationTests
{
    /// <summary>Verifies the paid date migration preserves the stored calendar date from a legacy timestamp.</summary>
    [Fact]
    public async Task Migration_PreservesLegacyPaidCalendarDate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync("20260628013521_InitialCreate");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Installments (TransactionId, CardId, TotalAmount, Periods, PerPeriod, RemainingPeriods, PurchaseDate, CreatedAt, Status, Description) " +
            "VALUES (NULL, NULL, 100, 1, 100, 1, '2026-06-01', '2026-06-01 00:00:00', 'Active', 'legacy')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO InstallmentPayments (InstallmentId, Period, Amount, PaidDate, IsPaid, DueDate) " +
            "VALUES (1, 1, 100, '2026-06-20 17:45:00', 1, '2026-06-20')");

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var payment = await db.InstallmentPayments.SingleAsync();

        Assert.Equal(new DateOnly(2026, 6, 20), payment.PaidDate);
    }
}
