using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class StockPriceUpdateServiceTests
{
    /// <summary>Verifies stock scheduling uses Taiwan market time independently of system time zone.</summary>
    [Fact]
    public void CalculateNextUpdateUtc_UsesTaiwanMarketTime()
    {
        var next = StockPriceUpdateService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 15, 5, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>Verifies stock scheduling skips weekends in the fixed Taiwan market zone.</summary>
    [Fact]
    public void CalculateNextUpdateUtc_SkipsTaiwanMarketWeekend()
    {
        var next = StockPriceUpdateService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 17, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 20, 15, 0, 0, DateTimeKind.Utc), next);
    }
}
