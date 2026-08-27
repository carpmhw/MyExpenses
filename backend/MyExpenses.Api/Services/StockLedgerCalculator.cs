using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>描述股票 Ledger replay 可能回傳的穩定錯誤代碼。</summary>
public enum StockLedgerFailureCode
{
    InvalidTransaction,
    InsufficientShares,
    NonFiniteResult,
}

/// <summary>表示 Ledger replay 失敗且可安全呈現給呼叫端的例外。</summary>
public class StockLedgerException : InvalidOperationException
{
    /// <summary>建立帶有穩定代碼與交易上下文的 Ledger 例外。</summary>
    public StockLedgerException(
        StockLedgerFailureCode failureCode,
        string message,
        int? transactionId = null,
        DateOnly? tradeDate = null,
        decimal? availableShares = null,
        decimal? requestedShares = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
        TransactionId = transactionId;
        TradeDate = tradeDate;
        AvailableShares = availableShares;
        RequestedShares = requestedShares;
    }

    public StockLedgerFailureCode FailureCode { get; }
    public string Code => FailureCode.ToString();
    public int? TransactionId { get; }
    public DateOnly? TradeDate { get; }
    public decimal? AvailableShares { get; }
    public decimal? RequestedShares { get; }
}

/// <summary>表示賣出股數超過交易當時可用部位的 typed failure。</summary>
public sealed class InsufficientSharesException : StockLedgerException
{
    /// <summary>建立包含可用與要求股數的 oversell 例外。</summary>
    public InsufficientSharesException(
        int transactionId,
        DateOnly tradeDate,
        decimal availableShares,
        decimal requestedShares)
        : base(
            StockLedgerFailureCode.InsufficientShares,
            "賣出股數超過交易當時可用部位",
            transactionId,
            tradeDate,
            availableShares,
            requestedShares)
    {
    }
}

/// <summary>描述 calculator 可接受的單筆原始 Ledger 交易。</summary>
public sealed record StockLedgerEntry(
    int Id,
    StockTransactionType Type,
    DateOnly TradeDate,
    int Sequence,
    decimal? Shares,
    decimal? Price,
    decimal Fee,
    decimal Tax,
    decimal? CashAmount = null,
    decimal? OpeningMarketValue = null,
    string? Notes = null);

/// <summary>描述一次完整股票 Ledger replay 的輸入。</summary>
public sealed record StockLedgerInput(IReadOnlyList<StockLedgerEntry> Entries);

/// <summary>描述 replay 後可投影回 Stock 的目前部位。</summary>
public sealed record StockLedgerProjection(
    decimal RemainingShares,
    decimal RemainingCostBasis,
    decimal ExecutionAveragePrice);

/// <summary>描述單筆交易 replay 後的衍生結果。</summary>
public sealed record StockLedgerEntryResult(
    StockLedgerEntry Entry,
    decimal GrossAmount,
    decimal NetCashFlow,
    decimal? AllocatedCostBasis,
    decimal RealizedGainLoss,
    decimal NetDividend,
    StockLedgerProjection Projection)
{
    public decimal RemainingShares => Projection.RemainingShares;
    public decimal RemainingCostBasis => Projection.RemainingCostBasis;
    public decimal ExecutionAveragePrice => Projection.ExecutionAveragePrice;
}

/// <summary>描述完整 replay 的部位、損益、股息與每筆衍生結果。</summary>
public sealed record StockLedgerResult(
    StockLedgerProjection Projection,
    decimal RealizedGainLoss,
    decimal NetDividendIncome,
    IReadOnlyList<StockLedgerEntryResult> Entries)
{
    public decimal RemainingShares => Projection.RemainingShares;
    public decimal RemainingCostBasis => Projection.RemainingCostBasis;
    public decimal ExecutionAveragePrice => Projection.ExecutionAveragePrice;
}

/// <summary>不依賴 persistence、HTTP 或 clock 的 deterministic moving-average replay。</summary>
public static class StockLedgerCalculator
{
    private const decimal ShareTolerance = 0.00000001m;

    /// <summary>依交易日期、同日順序與資料庫 ID 穩定重播 Ledger。</summary>
    public static StockLedgerResult Replay(StockLedgerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Replay(input.Entries);
    }

    /// <summary>依交易日期、同日順序與資料庫 ID 穩定重播原始交易集合。</summary>
    public static StockLedgerResult Replay(IEnumerable<StockLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var orderedEntries = entries
            .OrderBy(entry => entry.TradeDate)
            .ThenBy(entry => entry.Sequence)
            .ThenBy(entry => entry.Id)
            .ToList();
        var results = new List<StockLedgerEntryResult>(orderedEntries.Count);
        var remainingShares = 0m;
        var remainingCostBasis = 0m;
        var executionCostBasis = 0m;
        var realizedGainLoss = 0m;
        var netDividendIncome = 0m;

        try
        {
            foreach (var entry in orderedEntries)
            {
                Validate(entry);
                var grossAmount = 0m;
                var netCashFlow = 0m;
                var allocatedCostBasis = (decimal?)null;
                var entryRealizedGainLoss = 0m;
                var entryNetDividend = 0m;

                switch (entry.Type)
                {
                    case StockTransactionType.OpeningBalance:
                    {
                        var shares = entry.Shares!.Value;
                        var price = entry.Price!.Value;
                        grossAmount = shares * price;
                        remainingShares += shares;
                        remainingCostBasis += grossAmount;
                        executionCostBasis += grossAmount;
                        netCashFlow = -entry.OpeningMarketValue!.Value;
                        break;
                    }
                    case StockTransactionType.Buy:
                    {
                        var shares = entry.Shares!.Value;
                        var price = entry.Price!.Value;
                        grossAmount = shares * price;
                        var actualCost = grossAmount + entry.Fee + entry.Tax;
                        remainingShares += shares;
                        remainingCostBasis += actualCost;
                        executionCostBasis += grossAmount;
                        netCashFlow = -actualCost;
                        break;
                    }
                    case StockTransactionType.Sell:
                    {
                        var shares = entry.Shares!.Value;
                        var price = entry.Price!.Value;
                        if (shares > remainingShares + ShareTolerance)
                            throw new InsufficientSharesException(
                                entry.Id,
                                entry.TradeDate,
                                remainingShares,
                                shares);

                        grossAmount = shares * price;
                        netCashFlow = grossAmount - entry.Fee - entry.Tax;
                        allocatedCostBasis = remainingShares == 0m
                            ? 0m
                            : remainingCostBasis / remainingShares * shares;
                        var allocatedExecutionCost = remainingShares == 0m
                            ? 0m
                            : executionCostBasis / remainingShares * shares;
                        entryRealizedGainLoss = netCashFlow - allocatedCostBasis.Value;
                        remainingShares -= shares;
                        remainingCostBasis -= allocatedCostBasis.Value;
                        executionCostBasis -= allocatedExecutionCost;
                        realizedGainLoss += entryRealizedGainLoss;
                        break;
                    }
                    case StockTransactionType.Dividend:
                        grossAmount = entry.CashAmount!.Value;
                        entryNetDividend = grossAmount - entry.Fee - entry.Tax;
                        netCashFlow = entryNetDividend;
                        netDividendIncome += entryNetDividend;
                        break;
                    case StockTransactionType.StockDividend:
                        remainingShares += entry.Shares!.Value;
                        break;
                    default:
                        throw CreateInvalidTransactionException(entry, "交易類型無效");
                }

                if (remainingShares <= ShareTolerance)
                {
                    remainingShares = 0m;
                    remainingCostBasis = 0m;
                    executionCostBasis = 0m;
                }

                var projection = CreateProjection(
                    remainingShares,
                    remainingCostBasis,
                    executionCostBasis);
                results.Add(new StockLedgerEntryResult(
                    entry,
                    grossAmount,
                    netCashFlow,
                    allocatedCostBasis,
                    entryRealizedGainLoss,
                    entryNetDividend,
                    projection));
            }
        }
        catch (StockLedgerException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw new StockLedgerException(
                StockLedgerFailureCode.NonFiniteResult,
                "Ledger 計算結果超出 decimal 安全範圍",
                innerException: exception);
        }

        return new StockLedgerResult(
            CreateProjection(remainingShares, remainingCostBasis, executionCostBasis),
            realizedGainLoss,
            netDividendIncome,
            results);
    }

    /// <summary>將 EF 交易 entity 轉換成純 calculator 可使用的輸入並重播。</summary>
    public static StockLedgerResult Replay(IEnumerable<StockTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        return Replay(transactions.Select(ToEntry));
    }

    /// <summary>提供與其他 calculator 一致的 Calculate 別名。</summary>
    public static StockLedgerResult Calculate(StockLedgerInput input)
        => Replay(input);

    /// <summary>在 replay 前驗證單筆交易的型別專屬欄位。</summary>
    public static void Validate(StockLedgerEntry entry)
    {
        if (entry.Sequence < 0 || entry.Fee < 0m || entry.Tax < 0m)
            throw CreateInvalidTransactionException(entry, "交易順序或費稅欄位無效");

        switch (entry.Type)
        {
            case StockTransactionType.OpeningBalance:
                RequirePositive(entry.Shares, entry, "期初股數無效");
                RequirePositive(entry.Price, entry, "期初價格無效");
                RequirePositive(entry.OpeningMarketValue, entry, "期初市值無效");
                if (entry.CashAmount.HasValue)
                    throw CreateInvalidTransactionException(entry, "期初交易不可包含現金股息");
                break;
            case StockTransactionType.Buy:
            case StockTransactionType.Sell:
                RequirePositive(entry.Shares, entry, "交易股數無效");
                RequirePositive(entry.Price, entry, "成交價格無效");
                if (entry.CashAmount.HasValue || entry.OpeningMarketValue.HasValue)
                    throw CreateInvalidTransactionException(entry, "買賣交易包含禁止欄位");
                break;
            case StockTransactionType.Dividend:
                RequirePositive(entry.CashAmount, entry, "股息金額無效");
                if (entry.Shares.HasValue || entry.Price.HasValue || entry.OpeningMarketValue.HasValue)
                    throw CreateInvalidTransactionException(entry, "股息交易包含禁止欄位");
                break;
            case StockTransactionType.StockDividend:
                RequirePositive(entry.Shares, entry, "配股股數無效");
                if (entry.Price.HasValue || entry.CashAmount.HasValue || entry.OpeningMarketValue.HasValue
                    || entry.Fee != 0m || entry.Tax != 0m)
                {
                    throw CreateInvalidTransactionException(entry, "股票股利包含禁止欄位或非零費稅");
                }
                break;
            default:
                throw CreateInvalidTransactionException(entry, "交易類型無效");
        }
    }

    /// <summary>將 persistence entity 的原始欄位映射成純交易 record。</summary>
    private static StockLedgerEntry ToEntry(StockTransaction transaction)
        => new(
            transaction.Id,
            transaction.Type,
            transaction.TradeDate,
            transaction.Sequence,
            transaction.Shares,
            transaction.Price,
            transaction.Fee,
            transaction.Tax,
            transaction.CashAmount,
            transaction.OpeningMarketValue,
            transaction.Notes);

    /// <summary>建立交易後的 projection 並將極小殘值歸零。</summary>
    private static StockLedgerProjection CreateProjection(
        decimal remainingShares,
        decimal remainingCostBasis,
        decimal executionCostBasis)
    {
        if (remainingShares <= ShareTolerance)
            return new StockLedgerProjection(0m, 0m, 0m);

        var executionAveragePrice = executionCostBasis / remainingShares;
        return new StockLedgerProjection(
            remainingShares,
            remainingCostBasis < ShareTolerance ? 0m : remainingCostBasis,
            executionAveragePrice);
    }

    /// <summary>驗證指定 decimal nullable 欄位必須存在且為正值。</summary>
    private static void RequirePositive(decimal? value, StockLedgerEntry entry, string message)
    {
        if (!value.HasValue || value.Value <= 0m)
            throw CreateInvalidTransactionException(entry, message);
    }

    /// <summary>建立帶有穩定交易上下文的 validation 例外。</summary>
    private static StockLedgerException CreateInvalidTransactionException(
        StockLedgerEntry entry,
        string message)
        => new(
            StockLedgerFailureCode.InvalidTransaction,
            message,
            entry.Id,
            entry.TradeDate);
}
