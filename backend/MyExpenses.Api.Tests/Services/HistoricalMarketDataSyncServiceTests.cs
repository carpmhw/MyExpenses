using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class HistoricalMarketDataSyncServiceTests
{
    /// <summary>驗證同步排程固定使用台灣時區的 23:30。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_UsesTaiwanMarketTimeAt2330()
    {
        var next = HistoricalMarketDataSyncService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 15, 14, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 15, 15, 30, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證週末到達時會跳到下一個平日夜間。</summary>
    [Fact]
    public void CalculateNextUpdateUtc_SkipsWeekend()
    {
        var next = HistoricalMarketDataSyncService.CalculateNextUpdateUtc(
            new DateTime(2026, 7, 17, 15, 40, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 20, 15, 30, 0, DateTimeKind.Utc), next);
    }
}
