using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public sealed record StockValuationResult(
    decimal GrossMarketValue,
    decimal BuyCost,
    decimal BuyCommission,
    decimal SellCommission,
    decimal SecuritiesTransactionTax,
    decimal EstimatedNetSellValue,
    decimal EstimatedBuyCost,
    decimal EstimatedGainLoss);

public static class StockValuationCalculator
{
    private const decimal CommissionRate = 0.001425m;
    private const decimal CommissionDiscountMultiplier = 0.28m;
    private const decimal MinimumCommission = 20m;

    /// <summary>Calculates all estimated Taiwan transaction-cost valuation fields for one stock holding.</summary>
    public static StockValuationResult Calculate(Stock stock)
    {
        var grossMarketValue = stock.CurrentPrice * stock.Shares;
        var buyCost = stock.BuyPrice * stock.Shares;
        var buyCommission = CalculateCommission(buyCost);
        var sellCommission = CalculateCommission(grossMarketValue);
        var securitiesTransactionTax = CalculateSecuritiesTransactionTax(grossMarketValue, stock.InstrumentType);
        var estimatedNetSellValue = grossMarketValue - sellCommission - securitiesTransactionTax;
        var estimatedBuyCost = buyCost + buyCommission;
        var estimatedGainLoss = estimatedNetSellValue - estimatedBuyCost;

        return new StockValuationResult(
            grossMarketValue,
            buyCost,
            buyCommission,
            sellCommission,
            securitiesTransactionTax,
            estimatedNetSellValue,
            estimatedBuyCost,
            estimatedGainLoss);
    }

    /// <summary>Calculates one-side brokerage commission using the default 2.8 折 Taiwan brokerage assumptions.</summary>
    public static decimal CalculateCommission(decimal amount)
    {
        if (amount <= 0) return 0m;

        var discountedCommission = RoundMoney(amount * CommissionRate * CommissionDiscountMultiplier);
        return Math.Max(discountedCommission, MinimumCommission);
    }

    /// <summary>Calculates sell-side securities transaction tax based on the holding instrument type.</summary>
    public static decimal CalculateSecuritiesTransactionTax(decimal sellAmount, StockInstrumentType instrumentType)
    {
        if (sellAmount <= 0) return 0m;

        return RoundMoney(sellAmount * GetSecuritiesTransactionTaxRate(instrumentType));
    }

    /// <summary>Returns the Taiwan securities transaction tax rate for a supported instrument type.</summary>
    public static decimal GetSecuritiesTransactionTaxRate(StockInstrumentType instrumentType)
    {
        return instrumentType switch
        {
            StockInstrumentType.Stock => 0.003m,
            StockInstrumentType.StockEtf => 0.001m,
            StockInstrumentType.BondEtf => 0m,
            _ => 0.003m,
        };
    }

    /// <summary>Floors monetary fee and tax estimates to whole TWD.</summary>
    private static decimal RoundMoney(decimal value)
    {
        return Math.Floor(value);
    }
}
