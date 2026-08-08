namespace MyExpenses.Api.Models;

/// <summary>保存以市場與代號識別的還原權息日價格。</summary>
public sealed class HistoricalAdjustedPrice
{
    public long Id { get; set; }
    public StockMarket Market { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateOnly TradingDate { get; set; }
    public decimal AdjustedClose { get; set; }
    public string Provider { get; set; } = string.Empty;
    public DateTime FetchedAtUtc { get; set; }
}
