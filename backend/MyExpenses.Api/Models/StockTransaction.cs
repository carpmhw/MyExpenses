namespace MyExpenses.Api.Models;

/// <summary>保存股票 Ledger 的原始交易輸入與 UTC 稽核欄位。</summary>
public sealed class StockTransaction
{
    public int Id { get; set; }
    public int StockId { get; set; }
    public Stock Stock { get; set; } = null!;
    public StockTransactionType Type { get; set; }
    public DateOnly TradeDate { get; set; }
    public int Sequence { get; set; }
    public decimal? Shares { get; set; }
    public decimal? Price { get; set; }
    public decimal Fee { get; set; }
    public decimal Tax { get; set; }
    public decimal? CashAmount { get; set; }
    public decimal? OpeningMarketValue { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
