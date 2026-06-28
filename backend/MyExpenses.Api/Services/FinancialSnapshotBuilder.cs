using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public static class FinancialSnapshotBuilder
{
    /// <summary>Builds a snapshot batch from current bank and stock data without persisting it.</summary>
    public static SnapshotBatch Build(string name, string? notes, DateTime now, IEnumerable<BankAccount> bankAccounts, IEnumerable<Stock> stocks)
    {
        var bankDetails = bankAccounts.Select(b => new BankDetail
        {
            BankName = b.BankName,
            AccountNumber = b.AccountNumber,
            AccountType = b.AccountType,
            Balance = b.Balance,
        }).ToList();

        var totalBankBalance = bankDetails.Sum(b => b.Balance);
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
        var totalNetWorth = totalBankBalance + totalStockValue;

        return new SnapshotBatch
        {
            Name = name,
            SnapshotDate = now,
            Notes = notes,
            TotalNetWorth = totalNetWorth,
            TotalBankBalance = totalBankBalance,
            TotalStockValue = totalStockValue,
            TotalStockCost = totalStockCost,
            BankDetails = bankDetails,
            StockDetails = stockDetails,
        };
    }
}
