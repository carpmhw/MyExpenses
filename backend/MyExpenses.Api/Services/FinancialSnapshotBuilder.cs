using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public static class FinancialSnapshotBuilder
{
    /// <summary>使用 TWD identity 建立只含 TWD 或已由呼叫端確認的快照。</summary>
    public static SnapshotBatch Build(
        string name,
        string? notes,
        DateTime now,
        IEnumerable<BankAccount> bankAccounts,
        IEnumerable<Stock> stocks,
        decimal totalLiabilities = 0m)
        => Build(
            name,
            notes,
            now,
            bankAccounts,
            stocks,
            ExchangeRateSnapshot.Identity,
            totalLiabilities);

    /// <summary>使用同一匯率 snapshot 建立包含固定銀行估值的完整快照。</summary>
    public static SnapshotBatch Build(
        string name,
        string? notes,
        DateTime now,
        IEnumerable<BankAccount> bankAccounts,
        IEnumerable<Stock> stocks,
        ExchangeRateSnapshot exchangeRateSnapshot,
        decimal totalLiabilities = 0m)
    {
        ArgumentNullException.ThrowIfNull(bankAccounts);
        ArgumentNullException.ThrowIfNull(stocks);
        ArgumentNullException.ThrowIfNull(exchangeRateSnapshot);

        var accountValues = bankAccounts
            .Select(account => CreateBankDetail(account, exchangeRateSnapshot))
            .ToList();
        var containsForeignCurrency = accountValues.Any(detail =>
            detail.CurrencyCode != CurrencyPolicy.BaseCurrencyCode);
        var totalBankBalance = accountValues.Sum(detail => detail.ConvertedBalance);
        var stockValuations = stocks.Select(s => new
        {
            Stock = s,
            Valuation = StockValuationCalculator.Calculate(s),
        }).ToList();

        var stockDetails = stockValuations.Select(s => new StockDetail
        {
            Name = s.Stock.Name,
            Symbol = s.Stock.Symbol,
            InstrumentType = s.Stock.InstrumentType,
            Shares = s.Stock.Shares,
            BuyPrice = s.Stock.BuyPrice,
            CurrentPrice = s.Stock.CurrentPrice,
            MarketValue = s.Valuation.EstimatedNetSellValue,
            GainLoss = s.Valuation.EstimatedGainLoss,
        }).ToList();

        var totalStockValue = stockDetails.Sum(s => s.MarketValue);
        var totalStockCost = stockValuations.Sum(s => s.Valuation.EstimatedBuyCost);
        var totalAssets = totalBankBalance + totalStockValue;
        var totalNetWorth = totalAssets - totalLiabilities;

        return new SnapshotBatch
        {
            Name = name,
            SnapshotDate = now,
            Notes = notes,
            TotalAssets = totalAssets,
            TotalLiabilities = totalLiabilities,
            TotalNetWorth = totalNetWorth,
            NetWorthBasis = NetWorthBasis.AssetsMinusLiabilities,
            ExchangeRateUpdatedAt = containsForeignCurrency ? exchangeRateSnapshot.UpdatedAtUtc : null,
            ExchangeRateIsStale = containsForeignCurrency && exchangeRateSnapshot.IsStale,
            TotalBankBalance = totalBankBalance,
            TotalStockValue = totalStockValue,
            TotalStockCost = totalStockCost,
            BankDetails = accountValues,
            StockDetails = stockDetails,
        };
    }

    /// <summary>建立單一銀行帳戶的原幣與固定 TWD 快照明細。</summary>
    private static BankDetail CreateBankDetail(
        BankAccount account,
        ExchangeRateSnapshot exchangeRateSnapshot)
    {
        ArgumentNullException.ThrowIfNull(account);
        var currencyCode = CurrencyPolicy.NormalizeOrDefault(account.CurrencyCode);
        var rate = currencyCode == CurrencyPolicy.BaseCurrencyCode
            ? 1m
            : exchangeRateSnapshot.Rates.TryGetValue(currencyCode, out var foreignRate) && foreignRate > 0m
                ? foreignRate
                : throw new ExchangeRateUnavailableException($"缺少 {currencyCode} 匯率，無法建立快照");

        return new BankDetail
        {
            BankName = account.BankName,
            AccountNumber = account.AccountNumber,
            AccountType = account.AccountType,
            CurrencyCode = currencyCode,
            Balance = account.Balance,
            ExchangeRate = rate,
            BaseCurrencyCode = CurrencyPolicy.BaseCurrencyCode,
            ConvertedBalance = Math.Round(
                account.Balance / rate,
                2,
                MidpointRounding.AwayFromZero),
        };
    }
}
