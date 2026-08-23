using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class StockReportDataQualityCalculatorTests
{
    /// <summary>驗證空持股的正價格覆蓋率不可用且沒有時間範圍。</summary>
    [Fact]
    public void Calculate_ReturnsUnavailableCoverageForNoHoldings()
    {
        var result = StockReportDataQualityCalculator.Calculate(
            Array.Empty<Stock>(),
            new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, result.HoldingCount);
        Assert.Null(result.PositivePriceCoverage);
        Assert.Equal(0, result.MissingLastPriceUpdateCount);
        Assert.Equal(0, result.StalePriceCount);
        Assert.Equal(72, result.StaleAfterHours);
        Assert.Null(result.OldestLastPriceUpdateUtc);
        Assert.Null(result.LatestLastPriceUpdateUtc);
    }

    /// <summary>驗證正價格覆蓋、缺少時間、近期與超過 72 小時的時間戳分類。</summary>
    [Fact]
    public void Calculate_SeparatesMissingRecentAndStalePriceUpdates()
    {
        var asOfUtc = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var exactBoundary = asOfUtc.AddHours(-72);
        var stale = asOfUtc.AddHours(-72).AddTicks(-1);
        var result = StockReportDataQualityCalculator.Calculate(new[]
        {
            CreateStock(1, 100m, null),
            CreateStock(2, 0m, asOfUtc.AddHours(-1)),
            CreateStock(3, 50m, exactBoundary),
            CreateStock(4, 10m, stale),
        }, asOfUtc);

        Assert.Equal(4, result.HoldingCount);
        Assert.Equal(3, result.PositivePriceCount);
        Assert.Equal(0.75m, result.PositivePriceCoverage);
        Assert.Equal(1, result.MissingLastPriceUpdateCount);
        Assert.Equal(1, result.StalePriceCount);
        Assert.Equal(72, result.StaleAfterHours);
        Assert.Equal(stale, result.OldestLastPriceUpdateUtc);
        Assert.Equal(asOfUtc.AddHours(-1), result.LatestLastPriceUpdateUtc);
        Assert.Equal(asOfUtc, result.GeneratedAtUtc);
    }

    /// <summary>驗證 Local 更新時間與 Unspecified UTC 基準在 72 小時邊界前會正規化為 UTC。</summary>
    [Fact]
    public void Calculate_NormalizesLocalUpdatesAndUnspecifiedAsOfUtcBeforeClassifyingStaleness()
    {
        var asOfUtc = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var asOfUnspecified = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Unspecified);
        var exactBoundaryLocal = asOfUtc.AddHours(-72).ToLocalTime();
        var staleLocal = asOfUtc.AddHours(-72).AddTicks(-1).ToLocalTime();

        var result = StockReportDataQualityCalculator.Calculate(new[]
        {
            CreateStock(1, 100m, exactBoundaryLocal),
            CreateStock(2, 100m, staleLocal),
        }, asOfUnspecified);

        Assert.Equal(1, result.StalePriceCount);
        Assert.Equal(DateTimeKind.Utc, result.OldestLastPriceUpdateUtc!.Value.Kind);
        Assert.Equal(staleLocal.ToUniversalTime(), result.OldestLastPriceUpdateUtc.Value);
        Assert.Equal(DateTimeKind.Utc, result.LatestLastPriceUpdateUtc!.Value.Kind);
        Assert.Equal(exactBoundaryLocal.ToUniversalTime(), result.LatestLastPriceUpdateUtc.Value);
        Assert.Equal(asOfUtc, result.GeneratedAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.GeneratedAtUtc.Kind);
    }

    /// <summary>建立資料品質測試持股。</summary>
    private static Stock CreateStock(int id, decimal currentPrice, DateTime? lastPriceUpdate)
        => new()
        {
            Id = id,
            Name = $"標的 {id}",
            Symbol = $"S{id}",
            CurrentPrice = currentPrice,
            LastPriceUpdate = lastPriceUpdate,
        };
}
