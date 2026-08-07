namespace MyExpenses.Api.Models;

/// <summary>保存單一市場標的最近一次行情同步的安全狀態。</summary>
public sealed class HistoricalPriceSyncState
{
    public long Id { get; set; }
    public StockMarket Market { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateTime? LastAttemptedAtUtc { get; set; }
    public DateTime? LastSucceededAtUtc { get; set; }
    public DateOnly? LatestTradingDate { get; set; }
    public HistoricalPriceSyncStatus Status { get; set; }
    public string? SafeMessage { get; set; }
}
