using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class InstallmentDerivedStateTests
{
    /// <summary>Verifies list projections derive lifecycle state from current payment rows.</summary>
    [Fact]
    public async Task ListInstallments_DerivesRemainingPeriodsAndStatusFromPayments()
    {
        await using var db = await CreateDbContextAsync();
        var installment = new Installment
        {
            TotalAmount = 100m,
            Periods = 2,
            PerPeriod = 50m,
            PurchaseDate = new DateOnly(2026, 6, 20),
            Description = "衍生狀態",
        };
        db.Installments.Add(installment);
        await db.SaveChangesAsync();
        db.InstallmentPayments.AddRange(
            new InstallmentPayment
            {
                InstallmentId = installment.Id,
                Period = 1,
                Amount = 50m,
                IsPaid = true,
                PaidDate = new DateOnly(2026, 6, 21),
            },
            new InstallmentPayment
            {
                InstallmentId = installment.Id,
                Period = 2,
                Amount = 50m,
                IsPaid = false,
            });
        await db.SaveChangesAsync();

        var active = await InstallmentEndpoints.ListInstallmentsAsync(
            1, 10, null, null, null, "Active", db);

        var activeItem = Assert.Single(active.Items);
        Assert.Equal(1, activeItem.RemainingPeriods);
        Assert.Equal(InstallmentStatus.Active, activeItem.Status);
        Assert.Equal(1, activeItem.Periods - activeItem.RemainingPeriods);
        Assert.Equal(1, active.Summary.TotalCount);
        Assert.Equal(1, active.Summary.ActiveCount);

        var unpaid = await db.InstallmentPayments.SingleAsync(payment => !payment.IsPaid);
        unpaid.IsPaid = true;
        unpaid.PaidDate = new DateOnly(2026, 6, 22);
        await db.SaveChangesAsync();

        var paidOff = await InstallmentEndpoints.ListInstallmentsAsync(
            1, 10, null, null, null, "PaidOff", db);

        var paidOffItem = Assert.Single(paidOff.Items);
        Assert.Equal(0, paidOffItem.RemainingPeriods);
        Assert.Equal(InstallmentStatus.PaidOff, paidOffItem.Status);
        Assert.Equal(0, paidOff.Summary.ActiveCount);
    }

    /// <summary>驗證指定日期範圍與進行中狀態時仍包含範圍外的未繳交易。</summary>
    [Fact]
    public async Task ListInstallments_ActiveFilterIncludesUnpaidTransactionsOutsideDateRange()
    {
        await using var db = await CreateDbContextAsync();
        var installment = new Installment
        {
            TotalAmount = 100m,
            Periods = 1,
            PerPeriod = 100m,
            PurchaseDate = new DateOnly(2025, 1, 1),
            Description = "日期範圍外進行中交易",
        };
        db.Installments.Add(installment);
        await db.SaveChangesAsync();
        db.InstallmentPayments.Add(new InstallmentPayment
        {
            InstallmentId = installment.Id,
            Period = 1,
            Amount = 100m,
            IsPaid = false,
        });
        await db.SaveChangesAsync();

        var result = await InstallmentEndpoints.ListInstallmentsAsync(
            1,
            10,
            null,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            "Active",
            db);

        Assert.Single(result.Items);
        Assert.Equal(1, result.Summary.TotalCount);
        Assert.Equal(1, result.Summary.ActiveCount);
    }

    /// <summary>Creates an in-memory SQLite context for derived state endpoint tests.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
