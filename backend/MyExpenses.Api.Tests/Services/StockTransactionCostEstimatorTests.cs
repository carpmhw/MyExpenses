using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class StockTransactionCostEstimatorTests
{
    /// <summary>驗證 Buy 估算回傳 gross、最低以上佣金與明確零交易稅。</summary>
    [Fact]
    public void Estimate_Buy_ReturnsGrossCommissionAndZeroTax()
    {
        var result = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Buy,
            1000m,
            1050m,
            StockMarket.Twse,
            StockInstrumentType.Stock);

        Assert.Equal(StockTransactionCostEstimationStatus.Success, result.Status);
        Assert.Equal(1050000m, result.Estimate!.GrossAmount);
        Assert.Equal(418m, result.Estimate.Fee);
        Assert.Equal(0m, result.Estimate.Tax);
    }

    /// <summary>驗證 Sell Stock 使用既有佣金與 0.3% 證券交易稅規則。</summary>
    [Fact]
    public void Estimate_SellStock_UsesStockTax()
    {
        var result = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Sell,
            1000m,
            1050m,
            StockMarket.Twse,
            StockInstrumentType.Stock);

        Assert.Equal(StockTransactionCostEstimationStatus.Success, result.Status);
        Assert.Equal(418m, result.Estimate!.Fee);
        Assert.Equal(3150m, result.Estimate.Tax);
    }

    /// <summary>驗證 Sell Stock ETF 與 Bond ETF 套用各自既有交易稅率。</summary>
    [Fact]
    public void Estimate_SellEtfs_UsesInstrumentTaxRules()
    {
        var stockEtf = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Sell,
            1000m,
            1050m,
            StockMarket.Tpex,
            StockInstrumentType.StockEtf);
        var bondEtf = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Sell,
            1000m,
            1050m,
            StockMarket.Twse,
            StockInstrumentType.BondEtf);

        Assert.Equal(1050m, stockEtf.Estimate!.Tax);
        Assert.Equal(0m, bondEtf.Estimate!.Tax);
    }

    /// <summary>驗證低金額交易沿用既有每邊最低佣金。</summary>
    [Fact]
    public void Estimate_AppliesMinimumCommission()
    {
        var result = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Buy,
            1000m,
            1m,
            StockMarket.Twse,
            StockInstrumentType.Stock);

        Assert.Equal(20m, result.Estimate!.Fee);
    }

    /// <summary>驗證佣金與交易稅的小數金額沿用既有整數 TWD floor 行為。</summary>
    [Fact]
    public void Estimate_FloorsMonetaryValues()
    {
        var result = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Sell,
            1000m,
            1234.856m,
            StockMarket.Twse,
            StockInstrumentType.Stock);

        Assert.Equal(492m, result.Estimate!.Fee);
        Assert.Equal(3704m, result.Estimate.Tax);
    }

    /// <summary>驗證非正股數與價格會回傳 typed invalid result。</summary>
    [Fact]
    public void Estimate_RejectsNonPositiveInputs()
    {
        var invalidShares = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Buy,
            0m,
            100m,
            StockMarket.Twse,
            StockInstrumentType.Stock);
        var invalidPrice = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Buy,
            1m,
            -1m,
            StockMarket.Twse,
            StockInstrumentType.Stock);

        AssertInvalid(invalidShares, "NonPositiveShares");
        AssertInvalid(invalidPrice, "NonPositivePrice");
    }

    /// <summary>驗證未知市場、非買賣交易與未定義商品類型會回傳 typed unsupported result。</summary>
    [Fact]
    public void Estimate_RejectsUnsupportedMarketTypeAndInstrument()
    {
        var unknownMarket = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Buy,
            1m,
            100m,
            StockMarket.Unknown,
            StockInstrumentType.Stock);
        var dividend = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Dividend,
            1m,
            100m,
            StockMarket.Twse,
            StockInstrumentType.Stock);
        var undefinedInstrument = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Sell,
            1m,
            100m,
            StockMarket.Twse,
            (StockInstrumentType)99);

        AssertUnsupported(unknownMarket, "UnsupportedMarket");
        AssertUnsupported(dividend, "UnsupportedTransactionType");
        AssertUnsupported(undefinedInstrument, "UnsupportedInstrumentType");
    }

    /// <summary>驗證 StockDividend 明確回傳 unsupported 且不產生零費稅估算。</summary>
    [Fact]
    public void Estimate_StockDividend_ReturnsUnsupportedWithoutEstimate()
    {
        var result = StockTransactionCostEstimator.Estimate(
            StockTransactionType.StockDividend,
            100m,
            100m,
            StockMarket.Twse,
            StockInstrumentType.Stock);

        AssertUnsupported(result, "UnsupportedTransactionType");
        Assert.False(result.IsSuccess);
    }

    /// <summary>驗證 gross amount decimal overflow 會回傳 invalid result 而不產生零費稅。</summary>
    [Fact]
    public void Estimate_RejectsDecimalOverflow()
    {
        var result = StockTransactionCostEstimator.Estimate(
            StockTransactionType.Buy,
            decimal.MaxValue,
            2m,
            StockMarket.Twse,
            StockInstrumentType.Stock);

        AssertInvalid(result, "GrossAmountOverflow");
    }

    /// <summary>驗證 invalid result 不會夾帶可提交的估算值。</summary>
    private static void AssertInvalid(
        StockTransactionCostEstimationResult result,
        string reason)
    {
        Assert.Equal(StockTransactionCostEstimationStatus.InvalidInput, result.Status);
        Assert.Null(result.Estimate);
        Assert.Equal(reason, result.Reason);
    }

    /// <summary>驗證 unsupported result 不會夾帶可提交的估算值。</summary>
    private static void AssertUnsupported(
        StockTransactionCostEstimationResult result,
        string reason)
    {
        Assert.Equal(StockTransactionCostEstimationStatus.Unsupported, result.Status);
        Assert.Null(result.Estimate);
        Assert.Equal(reason, result.Reason);
    }
}
