using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>保存同一次取得的 TWSE 與 TPEx 官方市場清單結果。</summary>
public sealed record OfficialMarketCatalogSnapshot(
    CurrentPriceProviderResult Twse,
    CurrentPriceProviderResult Tpex);

/// <summary>保存單一代號的安全市場辨識結果。</summary>
public sealed record OfficialMarketResolution(
    StockMarket Market,
    string Code,
    CurrentPriceRecord? Record = null,
    bool Retryable = false,
    string SafeMessage = "市場辨識完成");

/// <summary>以官方雙市場 catalog 的 membership 純判定持股市場。</summary>
public static class OfficialMarketCatalogResolver
{
    /// <summary>依完整雙市場結果解析指定代號，不以價格或代號格式猜測市場。</summary>
    public static OfficialMarketResolution Resolve(
        OfficialMarketCatalogSnapshot snapshot,
        string? symbol)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (normalizedSymbol.Length == 0)
            return new(StockMarket.Unknown, "InvalidSymbol", SafeMessage: "股票代號格式無效");

        var unavailableCatalogs = new[] { snapshot.Twse, snapshot.Tpex }
            .Where(HasUnavailableCatalog)
            .ToArray();
        if (unavailableCatalogs.Length > 0)
        {
            var retryable = unavailableCatalogs.All(IsRetryable);
            return new(
                StockMarket.Unknown,
                "MarketDetectionUnavailable",
                Retryable: retryable,
                SafeMessage: "官方市場清單暫時無法使用");
        }

        var twseRecord = FindRecord(snapshot.Twse.Records, normalizedSymbol);
        var tpexRecord = FindRecord(snapshot.Tpex.Records, normalizedSymbol);
        if (twseRecord is not null && tpexRecord is not null)
            return new(StockMarket.Unknown, "AmbiguousMarket", SafeMessage: "市場辨識結果不唯一");
        if (twseRecord is not null)
            return new(StockMarket.Twse, "Completed", twseRecord);
        if (tpexRecord is not null)
            return new(StockMarket.Tpex, "Completed", tpexRecord);

        return new(StockMarket.Unknown, "MarketNotFound", SafeMessage: "找不到可辨識的交易市場");
    }

    /// <summary>判斷 provider 是否回傳完整可用的官方清單。</summary>
    private static bool HasUnavailableCatalog(CurrentPriceProviderResult result)
        => result.Failure is not null || result.Records.Count == 0;

    /// <summary>判斷官方清單 provider failure 是否可以由排程重試。</summary>
    private static bool IsRetryable(CurrentPriceProviderResult result)
        => result.Failure is not null
            ? result.Failure.Retryable
            : result.Records.Count == 0;

    /// <summary>從官方清單依正規化代號取得唯一 record。</summary>
    private static CurrentPriceRecord? FindRecord(
        IReadOnlyList<CurrentPriceRecord> records,
        string normalizedSymbol)
        => records
            .Where(record => NormalizeSymbol(record.Symbol) == normalizedSymbol)
            .LastOrDefault();

    /// <summary>正規化市場清單與持股使用的股票代號。</summary>
    public static string NormalizeSymbol(string? symbol)
        => symbol?.Trim().ToUpperInvariant() ?? string.Empty;
}
