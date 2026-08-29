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
    /// <summary>驗證 Dashboard 摘要完整彙總本期與前期的提款、支出及應付款。</summary>
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

    /// <summary>驗證 Dashboard 將本期與前期外幣提款換算為 TWD 並共用一次匯率 snapshot。</summary>
    [Fact]
    public async Task GetDashboardSummary_ConvertsCurrentAndPreviousWithdrawalsToTwd()
    {
        await using var db = await CreateDbContextAsync();
        var timeZone = CreateTimeZoneService();
        var account = new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "USD01",
            AccountType = "活期",
            Balance = 0m,
            CurrencyCode = "USD",
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();
        db.Withdrawals.AddRange(
            new Withdrawal
            {
                BankAccountId = account.Id,
                Amount = 310m,
                Date = new DateOnly(2026, 6, 5),
            },
            new Withdrawal
            {
                BankAccountId = account.Id,
                Amount = 155m,
                Date = new DateOnly(2026, 5, 5),
            });
        await db.SaveChangesAsync();
        var rates = new ExchangeRateSnapshot(
            CurrencyPolicy.BaseCurrencyCode,
            new Dictionary<string, decimal>
            {
                [CurrencyPolicy.BaseCurrencyCode] = 1m,
                ["USD"] = 0.031m,
            },
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            true);
        var exchangeRateService = new CountingExchangeRateService(rates);

        var result = await ReportEndpoints.GetDashboardSummaryAsync(
            2026,
            6,
            db,
            timeZone,
            exchangeRateService);

        Assert.Equal(10000m, result.TotalWithdrawals);
        Assert.Equal(10000m, result.DisposableBalance);
        Assert.Equal(5000m, result.PreviousDisposableBalance);
        Assert.Equal(CurrencyPolicy.BaseCurrencyCode, result.BaseCurrency);
        Assert.Equal(rates.UpdatedAtUtc, result.ExchangeRateUpdatedAt);
        Assert.True(result.ExchangeRateIsStale);
        Assert.True(result.ConversionAvailable);
        Assert.Equal(1, exchangeRateService.Calls);
    }

    /// <summary>驗證只有 TWD 提款時不呼叫外部匯率服務。</summary>
    [Fact]
    public async Task GetDashboardSummary_TwdWithdrawalsDoNotCallExchangeRateService()
    {
        await using var db = await CreateDbContextAsync();
        var account = new BankAccount
        {
            BankName = "台幣銀行",
            AccountNumber = "TWD01",
            AccountType = "活期",
            Balance = 0m,
            CurrencyCode = CurrencyPolicy.BaseCurrencyCode,
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();
        db.Withdrawals.Add(new Withdrawal
        {
            BankAccountId = account.Id,
            Amount = 1000m,
            Date = new DateOnly(2026, 6, 5),
        });
        await db.SaveChangesAsync();
        var exchangeRateService = new CountingExchangeRateService(ExchangeRateSnapshot.Identity);

        var result = await ReportEndpoints.GetDashboardSummaryAsync(
            2026,
            6,
            db,
            CreateTimeZoneService(),
            exchangeRateService);

        Assert.Equal(1000m, result.TotalWithdrawals);
        Assert.Equal(0, exchangeRateService.Calls);
        Assert.Null(result.ExchangeRateUpdatedAt);
        Assert.False(result.ExchangeRateIsStale);
    }

    /// <summary>驗證外幣提款缺率時 Dashboard 不回傳原幣直加總。</summary>
    [Fact]
    public async Task GetDashboardSummary_MissingForeignRateFailsClosed()
    {
        await using var db = await CreateDbContextAsync();
        var account = new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "USD01",
            AccountType = "活期",
            Balance = 0m,
            CurrencyCode = "USD",
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();
        db.Withdrawals.Add(new Withdrawal
        {
            BankAccountId = account.Id,
            Amount = 310m,
            Date = new DateOnly(2026, 6, 5),
        });
        await db.SaveChangesAsync();
        var exchangeRateService = new CountingExchangeRateService(new ExchangeRateSnapshot(
            CurrencyPolicy.BaseCurrencyCode,
            new Dictionary<string, decimal>
            {
                [CurrencyPolicy.BaseCurrencyCode] = 1m,
            },
            DateTime.UtcNow,
            false));

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            ReportEndpoints.GetDashboardSummaryAsync(
                2026,
                6,
                db,
                CreateTimeZoneService(),
                exchangeRateService));
    }

    /// <summary>驗證 Dashboard 摘要拒絕無效的日曆月份。</summary>
    [Fact]
    public async Task GetDashboardSummary_RejectsInvalidMonth()
    {
        await using var db = await CreateDbContextAsync();
        var timeZone = CreateTimeZoneService();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            ReportEndpoints.GetDashboardSummaryAsync(2026, 13, db, timeZone));

        Assert.Equal("月份必須介於 1 到 12 之間", error.Message);
    }

    /// <summary>驗證淨值趨勢選取各本地月份最新的完整快照。</summary>
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

    /// <summary>驗證淨值趨勢跨 UTC 月界時仍使用設定的本地日曆月份。</summary>
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

    /// <summary>建立報表摘要測試使用的記憶體 SQLite context。</summary>
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

    /// <summary>提供固定匯率 snapshot 並記錄取得次數的測試服務。</summary>
    private sealed class CountingExchangeRateService : IExchangeRateService
    {
        /// <summary>初始化固定匯率 snapshot。</summary>
        public CountingExchangeRateService(ExchangeRateSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        /// <summary>取得匯率 snapshot 的呼叫次數。</summary>
        public int Calls { get; private set; }

        /// <summary>取得測試使用的固定匯率 snapshot。</summary>
        public ExchangeRateSnapshot Snapshot { get; }

        /// <summary>記錄呼叫並回傳固定匯率 snapshot。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Snapshot);
        }

        /// <summary>依指定 snapshot 將原幣金額換算為 TWD。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => currencyCode == CurrencyPolicy.BaseCurrencyCode
                ? amount
                : snapshot.Rates.TryGetValue(currencyCode, out var rate) && rate > 0m
                    ? amount / rate
                    : null;

        /// <summary>嘗試依指定 snapshot 將原幣金額換算為 TWD。</summary>
        public bool TryConvertToBase(
            decimal amount,
            string currencyCode,
            ExchangeRateSnapshot snapshot,
            out decimal convertedAmount)
        {
            var converted = ConvertToBase(amount, currencyCode, snapshot);
            convertedAmount = converted.GetValueOrDefault();
            return converted.HasValue;
        }
    }

    /// <summary>建立報表測試使用的 Asia/Taipei 時區服務。</summary>
    private static TimeZoneService CreateTimeZoneService()
        => new(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" }));
}
