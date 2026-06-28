using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class StockValuationCalculatorTests
{
    /// <summary>Verifies regular stock net sell value deducts sell commission and 0.3% securities transaction tax.</summary>
    [Fact]
    public void Calculate_RegularStock_DeductsCommissionAndStockTax()
    {
        var stock = CreateStock(StockInstrumentType.Stock, buyPrice: 1000m, currentPrice: 1050m, shares: 1000m);

        var valuation = StockValuationCalculator.Calculate(stock);

        Assert.Equal(1050000m, valuation.GrossMarketValue);
        Assert.Equal(418m, valuation.SellCommission);
        Assert.Equal(3150m, valuation.SecuritiesTransactionTax);
        Assert.Equal(1046432m, valuation.EstimatedNetSellValue);
    }

    /// <summary>Verifies stock ETFs use the lower 0.1% securities transaction tax rate.</summary>
    [Fact]
    public void Calculate_StockEtf_UsesStockEtfTaxRate()
    {
        var stock = CreateStock(StockInstrumentType.StockEtf, buyPrice: 1000m, currentPrice: 1050m, shares: 1000m);

        var valuation = StockValuationCalculator.Calculate(stock);

        Assert.Equal(1050m, valuation.SecuritiesTransactionTax);
        Assert.Equal(1048532m, valuation.EstimatedNetSellValue);
    }

    /// <summary>Verifies bond ETFs do not deduct securities transaction tax.</summary>
    [Fact]
    public void Calculate_BondEtf_UsesZeroTaxRate()
    {
        var stock = CreateStock(StockInstrumentType.BondEtf, buyPrice: 1000m, currentPrice: 1050m, shares: 1000m);

        var valuation = StockValuationCalculator.Calculate(stock);

        Assert.Equal(0m, valuation.SecuritiesTransactionTax);
        Assert.Equal(1049582m, valuation.EstimatedNetSellValue);
    }

    /// <summary>Verifies estimated gain/loss deducts buy commission, sell commission, and sell-side tax.</summary>
    [Fact]
    public void Calculate_EstimatedGainLoss_DeductsAllEstimatedCosts()
    {
        var stock = CreateStock(StockInstrumentType.Stock, buyPrice: 1000m, currentPrice: 1050m, shares: 1000m);

        var valuation = StockValuationCalculator.Calculate(stock);

        Assert.Equal(399m, valuation.BuyCommission);
        Assert.Equal(46033m, valuation.EstimatedGainLoss);
    }

    /// <summary>Verifies commission and securities transaction tax decimals are floored to whole TWD.</summary>
    [Fact]
    public void Calculate_FeesAndTax_FloorsDecimalsToWholeTwd()
    {
        var stock = CreateStock(StockInstrumentType.Stock, buyPrice: 1000m, currentPrice: 1234m, shares: 1000m);

        var valuation = StockValuationCalculator.Calculate(stock);

        Assert.Equal(492m, valuation.SellCommission);
        Assert.Equal(3702m, valuation.SecuritiesTransactionTax);
        Assert.Equal(1229806m, valuation.EstimatedNetSellValue);
    }

    /// <summary>Verifies fee and tax decimals above half are still floored instead of rounded.</summary>
    [Fact]
    public void Calculate_FeesAndTax_FloorsDecimalsAboveHalfToWholeTwd()
    {
        var stock = CreateStock(StockInstrumentType.Stock, buyPrice: 1000m, currentPrice: 1234.856m, shares: 1000m);

        var valuation = StockValuationCalculator.Calculate(stock);

        Assert.Equal(492m, valuation.SellCommission);
        Assert.Equal(3704m, valuation.SecuritiesTransactionTax);
        Assert.Equal(1230660m, valuation.EstimatedNetSellValue);
    }

    /// <summary>Verifies discounted commission still respects the 20 TWD minimum for non-zero amounts.</summary>
    [Fact]
    public void Calculate_Commission_AppliesMinimumPerSide()
    {
        var stock = CreateStock(StockInstrumentType.Stock, buyPrice: 1m, currentPrice: 1m, shares: 1000m);

        var valuation = StockValuationCalculator.Calculate(stock);
        Assert.Equal(20m, valuation.BuyCommission);
        Assert.Equal(20m, valuation.SellCommission);
    }

    /// <summary>Verifies zero amounts do not receive the minimum commission.</summary>
    [Fact]
    public void Calculate_Commission_UsesZeroForZeroAmounts()
    {
        var stock = CreateStock(StockInstrumentType.Stock, buyPrice: 0m, currentPrice: 0m, shares: 1000m);

        var valuation = StockValuationCalculator.Calculate(stock);
        Assert.Equal(0m, valuation.BuyCommission);
        Assert.Equal(0m, valuation.SellCommission);
    }

    /// <summary>Creates a stock holding for valuation tests.</summary>
    private static Stock CreateStock(StockInstrumentType instrumentType, decimal buyPrice, decimal currentPrice, decimal shares)
    {
        return new Stock
        {
            Name = "測試標的",
            Symbol = "2330",
            InstrumentType = instrumentType,
            BuyPrice = buyPrice,
            CurrentPrice = currentPrice,
            Shares = shares,
        };
    }
}
