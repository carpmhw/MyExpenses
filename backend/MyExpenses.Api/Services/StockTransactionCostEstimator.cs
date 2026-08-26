using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>描述股票交易費稅估算的結果狀態。</summary>
public enum StockTransactionCostEstimationStatus
{
    Success,
    InvalidInput,
    Unsupported,
}

/// <summary>描述成功估算的交易金額與費稅。</summary>
public sealed record StockTransactionCostEstimate(
    decimal GrossAmount,
    decimal Fee,
    decimal Tax);

/// <summary>描述股票交易費稅估算的成功或 typed 失敗結果。</summary>
public sealed record StockTransactionCostEstimationResult(
    StockTransactionCostEstimationStatus Status,
    StockTransactionCostEstimate? Estimate = null,
    string? Reason = null)
{
    /// <summary>判斷結果是否包含可提交的估算值。</summary>
    public bool IsSuccess => Status == StockTransactionCostEstimationStatus.Success;
}

/// <summary>以既有估值 calculator 集中計算股票交易的預估費用與交易稅。</summary>
public static class StockTransactionCostEstimator
{
    /// <summary>驗證交易上下文並計算 gross、commission 與 sell-side tax。</summary>
    public static StockTransactionCostEstimationResult Estimate(
        StockTransactionType transactionType,
        decimal? shares,
        decimal? price,
        StockMarket market,
        StockInstrumentType instrumentType)
    {
        if (transactionType is not (StockTransactionType.Buy or StockTransactionType.Sell))
            return Unsupported("UnsupportedTransactionType");

        if (market is not (StockMarket.Twse or StockMarket.Tpex))
            return Unsupported("UnsupportedMarket");

        if (instrumentType is not (StockInstrumentType.Stock or StockInstrumentType.StockEtf or StockInstrumentType.BondEtf))
            return Unsupported("UnsupportedInstrumentType");

        if (!shares.HasValue)
            return Invalid("MissingShares");
        if (shares.Value <= 0m)
            return Invalid("NonPositiveShares");

        if (!price.HasValue)
            return Invalid("MissingPrice");
        if (price.Value <= 0m)
            return Invalid("NonPositivePrice");

        try
        {
            var grossAmount = shares.Value * price.Value;
            var fee = StockValuationCalculator.CalculateCommission(grossAmount);
            var tax = transactionType == StockTransactionType.Sell
                ? StockValuationCalculator.CalculateSecuritiesTransactionTax(grossAmount, instrumentType)
                : 0m;
            return new StockTransactionCostEstimationResult(
                StockTransactionCostEstimationStatus.Success,
                new StockTransactionCostEstimate(grossAmount, fee, tax));
        }
        catch (OverflowException)
        {
            return Invalid("GrossAmountOverflow");
        }
    }

    /// <summary>建立不帶估算值的 invalid input 結果。</summary>
    private static StockTransactionCostEstimationResult Invalid(string reason)
        => new(StockTransactionCostEstimationStatus.InvalidInput, Reason: reason);

    /// <summary>建立不帶估算值的 unsupported 結果。</summary>
    private static StockTransactionCostEstimationResult Unsupported(string reason)
        => new(StockTransactionCostEstimationStatus.Unsupported, Reason: reason);
}
