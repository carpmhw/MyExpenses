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

public class ReportEndpointsTests
{
    /// <summary>Verifies net-worth reporting uses stock estimated net sell values for stock assets.</summary>
    [Fact]
    public async Task GetNetWorth_UsesStockEstimatedNetSellValue()
    {
        await using var db = await CreateDbContextAsync();

        var result = await ReportEndpoints.GetNetWorthAsync(db);

        Assert.Equal(1047432m, result.TotalAssets);
        Assert.Equal(0m, result.TotalLiabilities);
        Assert.Equal(1047432m, result.NetWorth);

        var stock = Assert.Single(result.Stocks);
        Assert.Equal(StockInstrumentType.Stock, stock.InstrumentType);
        Assert.Equal(1050000m, stock.GrossMarketValue);
        Assert.Equal(1046432m, stock.EstimatedNetSellValue);
    }

    /// <summary>驗證淨值報表先將混合幣別銀行餘額換算為 TWD 再加總。</summary>
    [Fact]
    public async Task GetNetWorth_UsesConvertedBankBalances()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            Balance = 310m,
            CurrencyCode = "USD",
            AccountType = "活期",
        });
        await db.SaveChangesAsync();

        var result = await ReportEndpoints.GetNetWorthAsync(
            db,
            new FixedExchangeRateService(CreateRates(0.031m)));

        Assert.Equal(1057432m, result.TotalAssets);
        Assert.Equal(11000m, result.TotalBankBalance);
        Assert.Equal(CurrencyPolicy.BaseCurrencyCode, result.BaseCurrency);
        Assert.True(result.ConversionAvailable);
        var foreignAccount = Assert.Single(result.BankAccounts, account => account.CurrencyCode == "USD");
        Assert.Equal(310m, foreignAccount.Balance);
        Assert.Equal(10000m, foreignAccount.ConvertedBalance);
    }

    /// <summary>驗證淨值報表缺少必要匯率時不製造原幣直加總額。</summary>
    [Fact]
    public async Task GetNetWorth_WhenExchangeRateIsUnavailable_FailsClosed()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            Balance = 310m,
            CurrencyCode = "USD",
            AccountType = "活期",
        });
        await db.SaveChangesAsync();
        var service = new FixedExchangeRateService(new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m },
            DateTime.UtcNow,
            false));

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            ReportEndpoints.GetNetWorthAsync(db, service));
    }

    /// <summary>驗證月摘要的銀行總額使用 TWD 固定基準而非直接相加原幣。</summary>
    [Fact]
    public async Task GetMonthlySummary_UsesConvertedBankBalances()
    {
        await using var db = await CreateDbContextAsync();
        db.BankAccounts.Add(new BankAccount
        {
            BankName = "美元銀行",
            AccountNumber = "23456",
            Balance = 310m,
            CurrencyCode = "USD",
            AccountType = "活期",
        });
        await db.SaveChangesAsync();
        var timeZone = new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" }));

        var result = await ReportEndpoints.GetMonthlySummaryAsync(
            2026,
            6,
            db,
            timeZone,
            new FixedExchangeRateService(CreateRates(0.031m)));

        Assert.Equal(11000m, result.TotalBankBalance);
        Assert.Equal(CurrencyPolicy.BaseCurrencyCode, result.BaseCurrency);
        Assert.True(result.ConversionAvailable);
    }

    /// <summary>Creates a SQLite-backed context for net-worth report tests.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.BankAccounts.Add(new BankAccount { BankName = "測試銀行", AccountNumber = "12345", Balance = 1000m, AccountType = "活期" });
        db.Stocks.Add(new Stock { Name = "台積電", Symbol = "2330", InstrumentType = StockInstrumentType.Stock, Shares = 1000m, BuyPrice = 1000m, CurrentPrice = 1050m });
        await db.SaveChangesAsync();

        return db;
    }

    /// <summary>建立測試用 TWD 基準匯率 snapshot。</summary>
    private static ExchangeRateSnapshot CreateRates(decimal usdRate)
        => new(
            CurrencyPolicy.BaseCurrencyCode,
            new Dictionary<string, decimal> { [CurrencyPolicy.BaseCurrencyCode] = 1m, ["USD"] = usdRate },
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            false);

    /// <summary>提供固定匯率 snapshot 的報表測試服務。</summary>
    private sealed class FixedExchangeRateService : IExchangeRateService
    {
        /// <summary>初始化固定匯率服務。</summary>
        public FixedExchangeRateService(ExchangeRateSnapshot snapshot) => Snapshot = snapshot;

        /// <summary>保存測試用匯率 snapshot。</summary>
        public ExchangeRateSnapshot Snapshot { get; }

        /// <summary>回傳固定測試 snapshot。</summary>
        public Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot);

        /// <summary>依指定 snapshot 將金額換算為 TWD。</summary>
        public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
            => currencyCode == CurrencyPolicy.BaseCurrencyCode
                ? amount
                : snapshot.Rates.TryGetValue(currencyCode, out var rate) && rate > 0m ? amount / rate : null;

        /// <summary>回傳測試換算的成功狀態。</summary>
        public bool TryConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot, out decimal convertedAmount)
        {
            var result = ConvertToBase(amount, currencyCode, snapshot);
            convertedAmount = result.GetValueOrDefault();
            return result.HasValue;
        }
    }
}
