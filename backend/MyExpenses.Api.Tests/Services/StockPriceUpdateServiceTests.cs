using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class StockPriceUpdateServiceTests
{
    /// <summary>驗證目前股價排程使用台灣時間的 23:00 cutoff。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_UsesTaiwanMarketTime()
    {
        var next = StockPriceUpdateService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 15, 5, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證目前股價排程跳過台灣週末並使用 23:00。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_SkipsTaiwanMarketWeekend()
    {
        var next = StockPriceUpdateService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 17, 15, 40, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 20, 15, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證台灣平日 15:00 至 22:59 不會提前換日。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_BeforeCutoffKeepsSameDay()
    {
        var next = StockPriceUpdateService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 15, 7, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證超過一天的 delay 顯示使用總時數而非 TimeSpan.Hours。</summary>
    [Fact]
    public void FormatDelay_UsesTotalHoursForLongWait()
    {
        Assert.Equal("26h 5m", StockPriceUpdateService.FormatDelay(TimeSpan.FromHours(26) + TimeSpan.FromMinutes(5)));
    }
}
