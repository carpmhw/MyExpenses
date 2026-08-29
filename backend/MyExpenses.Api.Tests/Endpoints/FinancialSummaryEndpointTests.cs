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

public class FinancialSummaryEndpointTests
{
    /// <summary>Verifies transaction summaries include records outside the requested page.</summary>
    [Fact]
    public async Task ListTransactions_SummaryIncludesAllFilteredRecords()
    {
        await using var db = await CreateDbContextAsync();
        var category = new Category
        {
            Name = "餐飲",
            Type = CategoryType.Expense,
            Icon = "Utensils",
            Color = "#000000",
            SortOrder = 1,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        db.Transactions.AddRange(
            CreateTransaction(category.Id, 100m, "早餐"),
            CreateTransaction(category.Id, 200m, "午餐"),
            CreateTransaction(category.Id, 300m, "晚餐"));
        await db.SaveChangesAsync();

        var firstPage = await TransactionEndpoints.ListTransactionsAsync(
            categoryId: category.Id,
            startDate: new DateOnly(2026, 6, 1),
            endDate: new DateOnly(2026, 6, 30),
            search: null,
            type: TransactionType.Expense,
            page: 1,
            pageSize: 1,
            db);

        var secondPage = await TransactionEndpoints.ListTransactionsAsync(
            categoryId: category.Id,
            startDate: new DateOnly(2026, 6, 1),
            endDate: new DateOnly(2026, 6, 30),
            search: null,
            type: TransactionType.Expense,
            page: 2,
            pageSize: 1,
            db);

        Assert.Single(firstPage.Items);
        Assert.Single(secondPage.Items);
        Assert.Equal(3, firstPage.Summary.Count);
        Assert.Equal(600m, firstPage.Summary.TotalExpense);
        Assert.Equal(firstPage.Summary, secondPage.Summary);
    }

    /// <summary>Verifies empty filtered transaction results return a fresh zero-valued summary.</summary>
    [Fact]
    public async Task ListTransactions_EmptyResultReturnsZeroSummary()
    {
        await using var db = await CreateDbContextAsync();

        var result = await TransactionEndpoints.ListTransactionsAsync(
            categoryId: null,
            startDate: new DateOnly(2026, 6, 1),
            endDate: new DateOnly(2026, 6, 30),
            search: "不存在",
            type: TransactionType.Expense,
            page: 1,
            pageSize: 15,
            db);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Summary.Count);
        Assert.Equal(0m, result.Summary.TotalAmount);
        Assert.Equal(0m, result.Summary.MaxAmount);
    }

    /// <summary>Verifies withdrawal summaries use all matching rows for average and maximum.</summary>
    [Fact]
    public async Task ListWithdrawals_SummaryIsIndependentOfPage()
    {
        await using var db = await CreateDbContextAsync();
        var account = new BankAccount
        {
            BankName = "測試銀行",
            AccountNumber = "12345",
            AccountType = "活期",
            Balance = 0m,
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();

        db.Withdrawals.AddRange(
            CreateWithdrawal(account.Id, 100m),
            CreateWithdrawal(account.Id, 200m),
            CreateWithdrawal(account.Id, 300m));
        await db.SaveChangesAsync();

        var result = await WithdrawalEndpoints.ListWithdrawalsAsync(
            startDate: new DateOnly(2026, 6, 1),
            endDate: new DateOnly(2026, 6, 30),
            page: 1,
            pageSize: 1,
            db);

        Assert.Single(result.Items);
        Assert.Equal(3, result.Summary.Count);
        Assert.Equal(600m, result.Summary.TotalAmount);
        Assert.Equal(200m, result.Summary.AverageAmount);
        Assert.Equal(300m, result.Summary.MaxAmount);
    }

    /// <summary>驗證提款摘要對不同帳戶幣別先換算為 TWD 再計算總額與統計值。</summary>
    [Fact]
    public async Task ListWithdrawals_MixedCurrenciesUseConvertedSummary()
    {
        await using var db = await CreateDbContextAsync();
        var twdAccount = new BankAccount
        {
            BankName = "台灣銀行",
            AccountNumber = "12345",
            AccountType = "活期",
            Balance = 0m,
            CurrencyCode = "TWD",
        };
        var usdAccount = new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            AccountType = "活期",
            Balance = 0m,
            CurrencyCode = "USD",
        };
        db.BankAccounts.AddRange(twdAccount, usdAccount);
        await db.SaveChangesAsync();
        db.Withdrawals.AddRange(
            CreateWithdrawal(twdAccount.Id, 100m),
            CreateWithdrawal(usdAccount.Id, 31m));
        await db.SaveChangesAsync();

        var result = await WithdrawalEndpoints.ListWithdrawalsAsync(
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            1,
            15,
            db,
            new FixedExchangeRateService());

        Assert.Equal(1100m, result.Summary.TotalAmount);
        Assert.Equal(550m, result.Summary.AverageAmount);
        Assert.Equal(1000m, result.Summary.MaxAmount);
        Assert.Equal(CurrencyPolicy.BaseCurrencyCode, result.Summary.BaseCurrency);
        Assert.True(result.Summary.ConversionAvailable);
    }

    /// <summary>驗證提款摘要缺少必要匯率時不直接相加原幣金額。</summary>
    [Fact]
    public async Task ListWithdrawals_WhenExchangeRateIsUnavailable_FailsClosed()
    {
        await using var db = await CreateDbContextAsync();
        var account = new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            AccountType = "活期",
            Balance = 0m,
            CurrencyCode = "USD",
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();
        db.Withdrawals.Add(CreateWithdrawal(account.Id, 31m));
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            WithdrawalEndpoints.ListWithdrawalsAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                1,
                15,
                db,
                new MissingExchangeRateService()));
    }

    /// <summary>Verifies installment summaries use due payments rather than every active per-period value.</summary>
    [Fact]
    public async Task ListInstallments_SummaryUsesUnpaidDuePayments()
    {
        await using var db = await CreateDbContextAsync();
        var timeZone = CreateTimeZoneService();
        var currentMonth = GetCurrentMonth(timeZone);
        var nextMonth = currentMonth.start.AddMonths(1);

        db.Installments.AddRange(
            new Installment
            {
                TotalAmount = 900m,
                Periods = 3,
                PerPeriod = 300m,
                RemainingPeriods = 3,
                PurchaseDate = currentMonth.start,
                Status = InstallmentStatus.Active,
                Description = "本月分期",
            },
            new Installment
            {
                TotalAmount = 1200m,
                Periods = 4,
                PerPeriod = 300m,
                RemainingPeriods = 4,
                PurchaseDate = currentMonth.start,
                Status = InstallmentStatus.Active,
                Description = "下月分期",
            });
        await db.SaveChangesAsync();

        var installments = await db.Installments.OrderBy(i => i.Id).ToListAsync();
        db.InstallmentPayments.AddRange(
            new InstallmentPayment
            {
                InstallmentId = installments[0].Id,
                Period = 1,
                Amount = 300m,
                DueDate = currentMonth.start.AddDays(5),
                IsPaid = false,
            },
            new InstallmentPayment
            {
                InstallmentId = installments[0].Id,
                Period = 2,
                Amount = 300m,
                DueDate = currentMonth.start.AddDays(6),
                IsPaid = true,
            },
            new InstallmentPayment
            {
                InstallmentId = installments[1].Id,
                Period = 1,
                Amount = 300m,
                DueDate = nextMonth,
                IsPaid = false,
            });
        await db.SaveChangesAsync();

        var result = await InstallmentEndpoints.ListInstallmentsAsync(
            page: 1,
            pageSize: 1,
            cardId: null,
            dateStart: currentMonth.start,
            dateEnd: currentMonth.end,
            status: "Active",
            db,
            timeZone);

        Assert.Single(result.Items);
        Assert.Equal(2, result.Summary.TotalCount);
        Assert.Equal(2, result.Summary.ActiveCount);
        Assert.Equal(300m, result.Summary.DueAmount);
        Assert.Equal(1, result.Summary.DuePaymentCount);
    }

    /// <summary>Creates a transaction fixture for summary tests.</summary>
    private static Transaction CreateTransaction(int categoryId, decimal amount, string description)
        => new()
        {
            CategoryId = categoryId,
            Type = TransactionType.Expense,
            Amount = amount,
            Date = new DateOnly(2026, 6, 15),
            Description = description,
        };

    /// <summary>Creates a withdrawal fixture for summary tests.</summary>
    private static Withdrawal CreateWithdrawal(int bankAccountId, decimal amount)
        => new()
        {
            BankAccountId = bankAccountId,
            Amount = amount,
            Date = new DateOnly(2026, 6, 15),
            Description = "測試提款",
        };

    /// <summary>Creates an in-memory SQLite context for summary endpoint tests.</summary>
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

    /// <summary>Creates the configured time-zone service used by date-bound summary tests.</summary>
    private static TimeZoneService CreateTimeZoneService()
        => new(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" }));

    /// <summary>Gets the current calendar month in the configured test time zone.</summary>
    private static (DateOnly start, DateOnly end) GetCurrentMonth(TimeZoneService timeZone)
    {
        var today = timeZone.GetLocalDate();
        var start = new DateOnly(today.Year, today.Month, 1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    /// <summary>提供 USD 匯率供提款摘要測試換算。</summary>
    private sealed class FixedExchangeRateService : IExchangeRateService
    {
        /// <summary>取得固定的 TWD 基準匯率 snapshot。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ExchangeRateSnapshot(
                CurrencyPolicy.BaseCurrencyCode,
                new Dictionary<string, decimal>
                {
                    [CurrencyPolicy.BaseCurrencyCode] = 1m,
                    ["USD"] = 0.031m,
                },
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                false));

        /// <summary>依指定 snapshot 將提款金額換算為 TWD。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => currencyCode == CurrencyPolicy.BaseCurrencyCode
                ? amount
                : snapshot.Rates.TryGetValue(currencyCode, out var rate) && rate > 0m ? amount / rate : null;

        /// <summary>回傳提款換算的成功狀態。</summary>
        public bool TryConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot, out decimal convertedAmount)
        {
            var result = ConvertToBase(amount, currencyCode, snapshot);
            convertedAmount = result.GetValueOrDefault();
            return result.HasValue;
        }
    }

    /// <summary>回傳只含 TWD 的測試 snapshot 以模擬缺少外幣匯率。</summary>
    private sealed class MissingExchangeRateService : IExchangeRateService
    {
        /// <summary>回傳缺少 USD 的 TWD snapshot。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ExchangeRateSnapshot.Identity);

        /// <summary>缺少外幣匯率時回傳不可用。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => currencyCode == CurrencyPolicy.BaseCurrencyCode ? amount : null;

        /// <summary>回傳缺少外幣匯率的失敗狀態。</summary>
        public bool TryConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot, out decimal convertedAmount)
        {
            convertedAmount = 0m;
            return currencyCode == CurrencyPolicy.BaseCurrencyCode;
        }
    }
}
