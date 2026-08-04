using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class FinancialReportSummaryTests
{
    /// <summary>Verifies dashboard summaries aggregate withdrawals, expenses, and due payments for both periods.</summary>
    [Fact]
    public async Task GetDashboardSummary_AggregatesCompleteCurrentAndPreviousPeriods()
    {
        await using var db = await CreateDbContextAsync();
        var timeZone = CreateTimeZoneService();
        var category = new Category
        {
            Name = "測試支出",
            Type = CategoryType.Expense,
            Icon = "Circle",
            Color = "#000000",
            SortOrder = 1,
        };
        var account = new BankAccount
        {
            BankName = "測試銀行",
            AccountNumber = "12345",
            AccountType = "活期",
            Balance = 0m,
        };
        db.Categories.Add(category);
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();

        db.Transactions.AddRange(
            new Transaction { CategoryId = category.Id, Type = TransactionType.Expense, Amount = 400m, Date = new DateOnly(2026, 6, 5), Description = "六月一" },
            new Transaction { CategoryId = category.Id, Type = TransactionType.Expense, Amount = 600m, Date = new DateOnly(2026, 6, 6), Description = "六月二" },
            new Transaction { CategoryId = category.Id, Type = TransactionType.Expense, Amount = 100m, Date = new DateOnly(2026, 5, 6), Description = "五月" });
        db.Withdrawals.AddRange(
            new Withdrawal { BankAccountId = account.Id, Amount = 2000m, Date = new DateOnly(2026, 6, 5), Description = "六月提款" },
            new Withdrawal { BankAccountId = account.Id, Amount = 1200m, Date = new DateOnly(2026, 5, 5), Description = "五月提款" });
        await db.SaveChangesAsync();

        var installment = new Installment
        {
            TotalAmount = 900m,
            Periods = 3,
            PerPeriod = 300m,
            RemainingPeriods = 3,
            PurchaseDate = new DateOnly(2026, 6, 1),
            Status = InstallmentStatus.Active,
            Description = "六月分期",
        };
        db.Installments.Add(installment);
        await db.SaveChangesAsync();
        db.InstallmentPayments.Add(new InstallmentPayment
        {
            InstallmentId = installment.Id,
            Period = 1,
            Amount = 300m,
            DueDate = new DateOnly(2026, 6, 20),
            IsPaid = false,
        });
        await db.SaveChangesAsync();

        var result = await ReportEndpoints.GetDashboardSummaryAsync(2026, 6, db, timeZone);

        Assert.Equal(2000m, result.TotalWithdrawals);
        Assert.Equal(1, result.WithdrawalCount);
        Assert.Equal(1000m, result.TotalExpenses);
        Assert.Equal(2, result.ExpenseCount);
        Assert.Equal(1000m, result.DisposableBalance);
        Assert.Equal(300m, result.InstallmentDueAmount);
        Assert.Equal(1, result.InstallmentDuePaymentCount);
        Assert.Equal(1, result.ActiveInstallmentCount);
        Assert.Equal(1100m, result.PreviousDisposableBalance);
    }

    /// <summary>Verifies dashboard summary rejects invalid calendar months.</summary>
    [Fact]
    public async Task GetDashboardSummary_RejectsInvalidMonth()
    {
        await using var db = await CreateDbContextAsync();
        var timeZone = CreateTimeZoneService();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            ReportEndpoints.GetDashboardSummaryAsync(2026, 13, db, timeZone));

        Assert.Equal("月份必須介於 1 到 12 之間", error.Message);
    }

    /// <summary>Verifies net-worth trends select the latest complete snapshot in each local calendar month.</summary>
    [Fact]
    public async Task GetNetWorthTrend_UsesLatestCompletePointAndOmitsLegacySnapshots()
    {
        await using var db = await CreateDbContextAsync();
        var timeZone = CreateTimeZoneService();
        db.SnapshotBatches.AddRange(
            new SnapshotBatch
            {
                Name = "legacy",
                SnapshotDate = new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc),
                TotalAssets = 900m,
                TotalNetWorth = 900m,
                NetWorthBasis = NetWorthBasis.AssetsOnly,
            },
            new SnapshotBatch
            {
                Name = "june-old",
                SnapshotDate = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc),
                TotalAssets = 1000m,
                TotalLiabilities = 100m,
                TotalNetWorth = 900m,
                NetWorthBasis = NetWorthBasis.AssetsMinusLiabilities,
            },
            new SnapshotBatch
            {
                Name = "june-latest",
                SnapshotDate = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc),
                TotalAssets = 1200m,
                TotalLiabilities = 150m,
                TotalNetWorth = 1050m,
                NetWorthBasis = NetWorthBasis.AssetsMinusLiabilities,
            });
        await db.SaveChangesAsync();

        var result = await ReportEndpoints.GetNetWorthTrendAsync(2, db, timeZone, new DateOnly(2026, 6, 30));

        var point = Assert.Single(result);
        Assert.Equal("2026/06", point.Month);
        Assert.Equal("june-latest", point.Name);
        Assert.Equal(1200m, point.TotalAssets);
        Assert.Equal(150m, point.TotalLiabilities);
        Assert.Equal(1050m, point.NetWorth);
    }

    /// <summary>Verifies net-worth trend months use the configured local calendar across a UTC month boundary.</summary>
    [Fact]
    public async Task GetNetWorthTrend_UsesConfiguredLocalMonthBoundary()
    {
        await using var db = await CreateDbContextAsync();
        var timeZone = CreateTimeZoneService();
        db.SnapshotBatches.Add(new SnapshotBatch
        {
            Name = "台北六月凌晨",
            SnapshotDate = new DateTime(2026, 5, 31, 16, 30, 0, DateTimeKind.Utc),
            TotalAssets = 1000m,
            TotalLiabilities = 100m,
            TotalNetWorth = 900m,
            NetWorthBasis = NetWorthBasis.AssetsMinusLiabilities,
        });
        await db.SaveChangesAsync();

        var result = await ReportEndpoints.GetNetWorthTrendAsync(
            1,
            db,
            timeZone,
            new DateOnly(2026, 6, 30));

        var point = Assert.Single(result);
        Assert.Equal("2026/06", point.Month);
    }

    /// <summary>Creates an in-memory SQLite context for report summary tests.</summary>
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

    /// <summary>Creates the configured Asia/Taipei service used by report tests.</summary>
    private static TimeZoneService CreateTimeZoneService()
        => new(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" }));
}
