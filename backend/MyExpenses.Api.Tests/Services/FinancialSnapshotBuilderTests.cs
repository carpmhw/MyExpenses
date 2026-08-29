using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class FinancialSnapshotBuilderTests
{
    /// <summary>Verifies snapshot builder subtracts captured unpaid liabilities from total assets.</summary>
    [Fact]
    public void Build_StoresCompleteAssetAndLiabilityBasis()
    {
        var snapshot = FinancialSnapshotBuilder.Build(
            "test",
            null,
            new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc),
            new[]
            {
                new BankAccount { BankName = "測試銀行", AccountNumber = "12345", AccountType = "活期", Balance = 1000m },
            },
            Array.Empty<Stock>(),
            250m);

        Assert.Equal(1000m, snapshot.TotalAssets);
        Assert.Equal(250m, snapshot.TotalLiabilities);
        Assert.Equal(750m, snapshot.TotalNetWorth);
        Assert.Equal(NetWorthBasis.AssetsMinusLiabilities, snapshot.NetWorthBasis);
    }

    /// <summary>驗證混合幣別快照以同一匯率 snapshot 保存原幣與 TWD 固定估值。</summary>
    [Fact]
    public void Build_StoresForeignCurrencyAndConvertedBalance()
    {
        var rates = new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m, ["USD"] = 0.031m },
            new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc),
            true);

        var snapshot = FinancialSnapshotBuilder.Build(
            "mixed",
            null,
            new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc),
            new[]
            {
                new BankAccount { BankName = "台灣銀行", AccountNumber = "12345", AccountType = "活期", CurrencyCode = "TWD", Balance = 100000m },
                new BankAccount { BankName = "美元銀行", AccountNumber = "23456", AccountType = "活期", CurrencyCode = "USD", Balance = 310m },
            },
            Array.Empty<Stock>(),
            rates);

        Assert.Equal(110000m, snapshot.TotalBankBalance);
        Assert.Equal(110000m, snapshot.TotalAssets);
        Assert.True(snapshot.ExchangeRateIsStale);
        Assert.Equal(rates.UpdatedAtUtc, snapshot.ExchangeRateUpdatedAt);
        var usd = Assert.Single(snapshot.BankDetails, detail => detail.CurrencyCode == "USD");
        Assert.Equal(310m, usd.Balance);
        Assert.Equal(0.031m, usd.ExchangeRate);
        Assert.Equal("TWD", usd.BaseCurrencyCode);
        Assert.Equal(10000m, usd.ConvertedBalance);
    }

    /// <summary>驗證快照固定估值在兩位小數邊界使用 AwayFromZero 捨入。</summary>
    [Fact]
    public void Build_RoundsConvertedBalanceAwayFromZero()
    {
        var rates = new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m, ["USD"] = 3m },
            DateTime.UtcNow,
            false);

        var snapshot = FinancialSnapshotBuilder.Build(
            "rounding",
            null,
            DateTime.UtcNow,
            new[]
            {
                new BankAccount { BankName = "正數", AccountNumber = "12345", CurrencyCode = "USD", Balance = 1m },
                new BankAccount { BankName = "負數", AccountNumber = "23456", CurrencyCode = "USD", Balance = -1m },
            },
            Array.Empty<Stock>(),
            rates);

        Assert.Equal(0.33m, snapshot.BankDetails[0].ConvertedBalance);
        Assert.Equal(-0.33m, snapshot.BankDetails[1].ConvertedBalance);
    }

    /// <summary>驗證缺少必要外幣匯率時快照 builder fail closed。</summary>
    [Fact]
    public void Build_RejectsMissingForeignCurrencyRate()
    {
        var rates = new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m },
            DateTime.UtcNow,
            false);

        Assert.Throws<ExchangeRateUnavailableException>(() => FinancialSnapshotBuilder.Build(
            "unavailable",
            null,
            DateTime.UtcNow,
            new[]
            {
                new BankAccount { BankName = "美元銀行", AccountNumber = "12345", CurrencyCode = "USD", Balance = 310m },
            },
            Array.Empty<Stock>(),
            rates));
    }
}
