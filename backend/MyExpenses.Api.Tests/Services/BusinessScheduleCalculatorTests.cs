using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class BusinessScheduleCalculatorTests
{
    /// <summary>驗證固定市場排程使用台灣時間並在 23:00 前保留當日 slot。</summary>
    [Fact]
    public void CalculateFixedNextRunUtc_UsesTaiwanCutoff()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        var next = BusinessScheduleCalculator.CalculateFixedNextRunUtc(
            new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc),
            new TimeOnly(23, 0),
            zone);

        Assert.Equal(new DateTime(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證停用快照沒有 next run 且啟用每月 31 日會 clamp 至月底。</summary>
    [Fact]
    public void CalculateAutomaticNextRunUtc_HandlesDisabledAndShortMonth()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        var disabled = new AutoSnapshotConfig { IsEnabled = false, Frequency = "Daily", TimeOfDay = "08:00" };
        Assert.Null(BusinessScheduleCalculator.CalculateAutomaticNextRunUtc(
            disabled,
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            zone));

        var monthly = new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Monthly",
            DayOfMonth = 31,
            TimeOfDay = "08:00",
        };
        var next = BusinessScheduleCalculator.CalculateAutomaticNextRunUtc(
            monthly,
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            zone);

        Assert.Equal(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>驗證 DST 不存在時間順延至同一本地日期第一個有效 instant。</summary>
    [Fact]
    public void ResolveLocalDateTimeUtc_ShiftsDstGapForward()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var utc = BusinessScheduleCalculator.ResolveLocalDateTimeUtc(
            new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified),
            zone);

        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), utc);
    }

    /// <summary>驗證 DST 重複時間採較早的 UTC instant。</summary>
    [Fact]
    public void ResolveLocalDateTimeUtc_UsesEarlierAmbiguousInstant()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var utc = BusinessScheduleCalculator.ResolveLocalDateTimeUtc(
            new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified),
            zone);

        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), utc);
    }

    /// <summary>驗證 descriptor 同時描述三個排程並使用後端計算的 next run。</summary>
    [Fact]
    public void CreateDescriptors_ReturnsAllBusinessSchedules()
    {
        var config = new AutoSnapshotConfig { IsEnabled = false, Frequency = "Daily", TimeOfDay = "08:00" };
        var descriptors = BusinessScheduleDescriptorFactory.Create(
            config,
            new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"));

        Assert.Equal(3, descriptors.Count);
        Assert.Contains(descriptors, descriptor => descriptor.JobKey == ScheduledJobKey.AutomaticSnapshot
            && descriptor.NextRunAtUtc is null
            && !descriptor.IsEnabled);
        Assert.Contains(descriptors, descriptor => descriptor.JobKey == ScheduledJobKey.StockPriceUpdate
            && descriptor.NextRunAtUtc == new DateTime(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc));
        Assert.Contains(descriptors, descriptor => descriptor.JobKey == ScheduledJobKey.HistoricalMarketDataSync
            && descriptor.NextRunAtUtc == new DateTime(2026, 7, 15, 15, 30, 0, DateTimeKind.Utc));
    }
}
