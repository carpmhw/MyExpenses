using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class BankAccountEndpointsTests
{
    /// <summary>Verifies bank account list queries return only accounts matching the bank name filter.</summary>
    [Fact]
    public async Task ListBankAccounts_FiltersByBankNameContainsText()
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, "國泰", db);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, account => Assert.Contains("國泰", account.BankName));
    }

    /// <summary>驗證銀行帳戶列表可依幣別篩選，且會正規化查詢幣別大小寫。</summary>
    [Fact]
    public async Task ListBankAccounts_FiltersByCurrencyCode()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "45678",
            Balance = 310m,
            CurrencyCode = "USD",
            AccountType = "活期",
        });
        await db.SaveChangesAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, null, db, null, " usd ");

        var item = Assert.Single(result.Items);
        Assert.Equal("USD", item.CurrencyCode);
        Assert.Equal("美元銀行", item.BankName);
        Assert.Equal(1, result.Total);
    }

    /// <summary>驗證銀行名稱與幣別條件會共同套用於列表查詢。</summary>
    [Fact]
    public async Task ListBankAccounts_CombinesBankNameAndCurrencyFilters()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.AddRange(
            new BankAccount
            {
                BankName = "國泰外幣",
                AccountNumber = "45678",
                Balance = 310m,
                CurrencyCode = "USD",
                AccountType = "活期",
            },
            new BankAccount
            {
                BankName = "玉山外幣",
                AccountNumber = "56789",
                Balance = 200m,
                CurrencyCode = "USD",
                AccountType = "活期",
            });
        await db.SaveChangesAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, "國泰", db, null, "USD");

        var item = Assert.Single(result.Items);
        Assert.Equal("國泰外幣", item.BankName);
        Assert.Equal("USD", item.CurrencyCode);
        Assert.Equal(1, result.Total);
    }

    /// <summary>Verifies filtered totals are counted before pagination is applied.</summary>
    [Fact]
    public async Task ListBankAccounts_CountsAllMatchingAccountsBeforePagination()
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 1, "國泰", db);

        Assert.Single(result.Items);
        Assert.Equal(2, result.Total);
    }

    /// <summary>Verifies filtered balance totals are summed before pagination is applied.</summary>
    [Fact]
    public async Task ListBankAccounts_SumsAllMatchingBalancesBeforePagination()
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 1, "國泰", db);

        Assert.Equal(3000m, result.TotalBalanceInBaseCurrency);
    }

    /// <summary>Verifies blank and missing bank name filters return all accounts and full balance totals.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListBankAccounts_BlankBankNameReturnsAllAccounts(string? bankName)
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, bankName, db);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(3, result.Total);
        Assert.Equal(6000m, result.TotalBalanceInBaseCurrency);
    }

    /// <summary>驗證列表會將混合幣別換算為 TWD 並在分頁前計算完整總額。</summary>
    [Fact]
    public async Task ListBankAccounts_ConvertsMixedCurrenciesBeforePagination()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.AddRange(
            new BankAccount
            {
                BankName = "台灣銀行",
                AccountNumber = "45678",
                Balance = 100000m,
                CurrencyCode = "TWD",
                AccountType = "活期",
            },
            new BankAccount
            {
                BankName = "美元銀行",
                AccountNumber = "56789",
                Balance = 310m,
                CurrencyCode = "USD",
                AccountType = "活期",
            });
        await db.SaveChangesAsync();
        var service = new FixedExchangeRateService(new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m, ["USD"] = 0.031m },
            DateTime.UtcNow,
            false));

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 1, null, db, service);

        Assert.Single(result.Items);
        Assert.Equal(5, result.Total);
        Assert.Equal(116000m, result.TotalBalanceInBaseCurrency);
        Assert.Equal("TWD", result.BaseCurrency);
        Assert.True(result.ConversionAvailable);
        Assert.Equal(1000m, result.Items[0].ConvertedBalance);

        var allItems = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, null, db, service);
        Assert.Equal(10000m, Assert.Single(allItems.Items, item => item.CurrencyCode == "USD").ConvertedBalance);
    }

    /// <summary>驗證只有 TWD 帳戶時列表可直接換算且不需要匯率服務。</summary>
    [Fact]
    public async Task ListBankAccounts_TwdOnlyDoesNotRequireExchangeRateService()
    {
        await using var db = await CreateDbContextAsync();

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, "國泰", db);

        Assert.Equal(3000m, result.TotalBalanceInBaseCurrency);
        Assert.All(result.Items, item => Assert.Equal(item.Balance, item.ConvertedBalance));
        Assert.True(result.ConversionAvailable);
        Assert.Null(result.ExchangeRateUpdatedAt);
    }

    /// <summary>驗證外幣匯率不可用時保留原幣項目但不製造折合總額。</summary>
    [Fact]
    public async Task ListBankAccounts_ReturnsPartialResponseWhenExchangeRateUnavailable()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "45678",
            Balance = 310m,
            CurrencyCode = "USD",
            AccountType = "活期",
        });
        await db.SaveChangesAsync();
        var service = new FixedExchangeRateService(new ExchangeRateUnavailableException());

        var result = await BankAccountEndpoints.ListBankAccountsAsync(1, 10, "美元", db, service);

        var item = Assert.Single(result.Items);
        Assert.Equal("USD", item.CurrencyCode);
        Assert.Equal(310m, item.Balance);
        Assert.Null(item.ConvertedBalance);
        Assert.Null(result.TotalBalanceInBaseCurrency);
        Assert.False(result.ConversionAvailable);
    }

    /// <summary>提供固定匯率或固定失敗的列表測試服務。</summary>
    private sealed class FixedExchangeRateService : IExchangeRateService
    {
        private readonly ExchangeRateSnapshot? _snapshot;
        private readonly Exception? _exception;

        /// <summary>初始化成功匯率 snapshot。</summary>
        public FixedExchangeRateService(ExchangeRateSnapshot snapshot) => _snapshot = snapshot;

        /// <summary>初始化固定匯率服務例外。</summary>
        public FixedExchangeRateService(Exception exception) => _exception = exception;

        /// <summary>回傳測試 snapshot 或拋出固定例外。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => _exception is not null
                ? Task.FromException<ExchangeRateSnapshot>(_exception)
                : Task.FromResult(_snapshot!);

        /// <summary>使用測試 snapshot 執行 TWD 換算。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => new ExchangeRateService(new NoopExchangeRateProvider()).ConvertToBase(amount, currencyCode, snapshot);

        /// <summary>回傳測試 snapshot 的換算可用狀態。</summary>
        public bool TryConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot, out decimal convertedAmount)
        {
            var result = ConvertToBase(amount, currencyCode, snapshot);
            convertedAmount = result.GetValueOrDefault();
            return result.HasValue;
        }
    }

    /// <summary>提供不會被列表測試呼叫的空 provider。</summary>
    private sealed class NoopExchangeRateProvider : IExchangeRateProvider
    {
        /// <summary>模擬不應發生的 provider 呼叫。</summary>
        public Task<ExchangeRateProviderResult> FetchAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("測試不應呼叫 provider");
    }

    /// <summary>Creates a seeded SQLite-backed context for bank account list query tests.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.BankAccounts.AddRange(
            new BankAccount { BankName = "國泰世華", AccountNumber = "12345", Balance = 1000m, AccountType = "活期存款", CreatedAt = new DateTime(2026, 1, 3), UpdatedAt = new DateTime(2026, 1, 3) },
            new BankAccount { BankName = "國泰銀行", AccountNumber = "23456", Balance = 2000m, AccountType = "數位帳戶", CreatedAt = new DateTime(2026, 1, 2), UpdatedAt = new DateTime(2026, 1, 2) },
            new BankAccount { BankName = "玉山銀行", AccountNumber = "34567", Balance = 3000m, AccountType = "定期存款", CreatedAt = new DateTime(2026, 1, 1), UpdatedAt = new DateTime(2026, 1, 1) });
        await db.SaveChangesAsync();

        return db;
    }
}