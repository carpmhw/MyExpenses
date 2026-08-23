using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public static class StockReportDataQualityCalculator
{
    private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromHours(72);

    /// <summary>以指定 UTC 基準時間計算持股價格的完整性與更新時間摘要。</summary>
    public static StockReportDataQuality Calculate(IEnumerable<Stock> stocks, DateTime asOfUtc)
    {
        var holdings = stocks.ToList();
        var normalizedAsOfUtc = NormalizeUtc(asOfUtc);
        var updates = holdings
            .Where(stock => stock.LastPriceUpdate.HasValue)
            .Select(stock => NormalizeUtc(stock.LastPriceUpdate!.Value))
            .ToList();
        var holdingCount = holdings.Count;
        var positivePriceCount = holdings.Count(stock => stock.CurrentPrice > 0m);

        return new StockReportDataQuality(
            holdingCount,
            positivePriceCount,
            holdingCount == 0 ? null : positivePriceCount / (decimal)holdingCount,
            holdings.Count(stock => !stock.LastPriceUpdate.HasValue),
            updates.Count(update => normalizedAsOfUtc - update > FreshnessThreshold),
            (int)FreshnessThreshold.TotalHours,
            updates.Count == 0 ? null : updates.Min(),
            updates.Count == 0 ? null : updates.Max(),
            normalizedAsOfUtc);
    }

    /// <summary>將 Local 轉換並將 SQLite 還原的 Unspecified 時間明確視為 UTC。</summary>
    internal static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

public sealed record StockReportDataQuality(
    int HoldingCount,
    int PositivePriceCount,
    decimal? PositivePriceCoverage,
    int MissingLastPriceUpdateCount,
    int StalePriceCount,
    int StaleAfterHours,
    DateTime? OldestLastPriceUpdateUtc,
    DateTime? LatestLastPriceUpdateUtc,
    DateTime GeneratedAtUtc);
