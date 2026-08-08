namespace MyExpenses.Api.Models;

/// <summary>描述台灣股票標的所屬的交易市場。</summary>
public enum StockMarket
{
    Unknown,
    Twse,
    Tpex,
}

/// <summary>描述歷史行情同步的安全狀態代碼。</summary>
public enum HistoricalPriceSyncStatus
{
    Success,
    ProviderError,
    InvalidResponse,
    NoData,
    AmbiguousMarket,
}
