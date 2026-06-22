namespace MyExpenses.Api.Models;

public class SnapshotBatch
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime SnapshotDate { get; set; }
    public string? Notes { get; set; }
    public decimal TotalNetWorth { get; set; }
    public decimal TotalBankBalance { get; set; }
    public decimal TotalStockValue { get; set; }
    public decimal TotalStockCost { get; set; }
    public List<BankDetail> BankDetails { get; set; } = [];
    public List<StockDetail> StockDetails { get; set; } = [];
}

public class BankDetail
{
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
}

public class StockDetail
{
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal Shares { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal MarketValue { get; set; }
    public decimal GainLoss { get; set; }
}
