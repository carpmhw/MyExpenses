using System.Globalization;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>集中計算三個業務排程的本地 wall-clock 與 UTC slot。</summary>
public static class BusinessScheduleCalculator
{
    private static readonly TimeOnly StockPriceTime = new(23, 0);
    private static readonly TimeOnly HistoricalSyncTime = new(23, 30);

    /// <summary>計算固定時區平日排程的下一個 UTC instant。</summary>
    public static DateTime CalculateFixedNextRunUtc(
        DateTime utcNow,
        TimeOnly localTime,
        TimeZoneInfo timeZone)
    {
        var normalizedNow = NormalizeUtc(utcNow);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(normalizedNow, timeZone);
        for (var offset = 0; offset <= 7; offset++)
        {
            var localDate = localNow.Date.AddDays(offset);
            if (localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            var candidate = ResolveLocalDateTimeUtc(
                DateTime.SpecifyKind(localDate.Add(localTime.ToTimeSpan()), DateTimeKind.Unspecified),
                timeZone);
            if (candidate > normalizedNow)
                return candidate;
        }

        throw new InvalidOperationException("Unable to calculate the next weekday schedule.");
    }

    /// <summary>依自動快照設定計算系統時區的下一個 UTC instant。</summary>
    public static DateTime? CalculateAutomaticNextRunUtc(
        AutoSnapshotConfig config,
        DateTime utcNow,
        TimeZoneInfo timeZone)
    {
        if (!config.IsEnabled || !TryParseTime(config.TimeOfDay, out var timeOfDay))
            return null;

        var normalizedNow = NormalizeUtc(utcNow);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(normalizedNow, timeZone);
        for (var offset = 0; offset <= 370; offset++)
        {
            var localDate = localNow.Date.AddDays(offset);
            if (!MatchesDate(config, localDate))
                continue;

            var candidate = ResolveLocalDateTimeUtc(
                DateTime.SpecifyKind(localDate.Add(timeOfDay.ToTimeSpan()), DateTimeKind.Unspecified),
                timeZone);
            if (candidate > normalizedNow)
                return candidate;
        }

        return null;
    }

    /// <summary>取得目前已到期自動快照本地日期對應的 UTC slot。</summary>
    public static DateTime? CalculateDueAutomaticSlotUtc(
        AutoSnapshotConfig config,
        DateTime utcNow,
        TimeZoneInfo timeZone)
    {
        if (!config.IsEnabled || !TryParseTime(config.TimeOfDay, out var timeOfDay))
            return null;

        var normalizedNow = NormalizeUtc(utcNow);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(normalizedNow, timeZone);
        if (!MatchesDate(config, localNow.Date))
            return null;

        var scheduledUtc = ResolveLocalDateTimeUtc(
            DateTime.SpecifyKind(localNow.Date.Add(timeOfDay.ToTimeSpan()), DateTimeKind.Unspecified),
            timeZone);
        return normalizedNow >= scheduledUtc ? scheduledUtc : null;
    }

    /// <summary>依 DST gap/ambiguous policy 將本地 wall-clock 解析成 UTC instant。</summary>
    public static DateTime ResolveLocalDateTimeUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(unspecified))
        {
            var shifted = unspecified;
            while (timeZone.IsInvalidTime(shifted) && shifted.Date == unspecified.Date)
                shifted = shifted.AddMinutes(1);
            if (shifted.Date != unspecified.Date)
                throw new InvalidOperationException("DST gap does not contain a valid instant on the scheduled date.");
            unspecified = shifted;
        }

        if (timeZone.IsAmbiguousTime(unspecified))
        {
            var earlierOffset = timeZone.GetAmbiguousTimeOffsets(unspecified).Max();
            return DateTime.SpecifyKind(
                new DateTimeOffset(unspecified, earlierOffset).UtcDateTime,
                DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone),
            DateTimeKind.Utc);
    }

    /// <summary>判斷指定本地日期是否符合自動快照頻率與 clamp 語意。</summary>
    public static bool MatchesDate(AutoSnapshotConfig config, DateTime localDate)
        => config.Frequency switch
        {
            "Daily" => true,
            "Weekly" => config.DayOfWeek is >= 0 and <= 6
                && (int)localDate.DayOfWeek == config.DayOfWeek.Value,
            "Monthly" => config.DayOfMonth is >= 1 and <= 31
                && localDate.Day == Math.Min(config.DayOfMonth.Value, DateTime.DaysInMonth(localDate.Year, localDate.Month)),
            _ => false,
        };

    /// <summary>取得固定市場目前價格下一次排程的 UTC instant。</summary>
    public static DateTime CalculateStockPriceNextRunUtc(DateTime utcNow)
        => CalculateFixedNextRunUtc(utcNow, StockPriceTime, TaiwanTimeZone);

    /// <summary>取得固定市場歷史行情下一次排程的 UTC instant。</summary>
    public static DateTime CalculateHistoricalSyncNextRunUtc(DateTime utcNow)
        => CalculateFixedNextRunUtc(utcNow, HistoricalSyncTime, TaiwanTimeZone);

    /// <summary>固定台灣市場時區的識別與解析。</summary>
    public static TimeZoneInfo TaiwanTimeZone { get; } =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    /// <summary>解析嚴格 HH:mm 的自動快照時間。</summary>
    private static bool TryParseTime(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);

    /// <summary>將日期時間標準化為 UTC kind。</summary>
    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Local)
            value = value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}

/// <summary>描述排程監控頁可顯示的唯讀排程設定與 next run。</summary>
public sealed record BusinessScheduleDescriptor(
    ScheduledJobKey JobKey,
    string DisplayName,
    string ConfigurationSource,
    bool IsEnabled,
    string FrequencyDescription,
    string ScheduleTimeZoneId,
    DateTime? NextRunAtUtc);

/// <summary>建立與 hosted services 共用計算器的三個業務排程 descriptor。</summary>
public static class BusinessScheduleDescriptorFactory
{
    /// <summary>依目前設定與 UTC 現在時間建立三個唯讀 descriptor。</summary>
    public static IReadOnlyList<BusinessScheduleDescriptor> Create(
        AutoSnapshotConfig? config,
        DateTime utcNow,
        TimeZoneInfo systemTimeZone)
    {
        var safeConfig = config ?? new AutoSnapshotConfig();
        var automaticDescription = BuildAutomaticDescription(safeConfig);
        return
        [
            new BusinessScheduleDescriptor(
                ScheduledJobKey.AutomaticSnapshot,
                "自動財務快照",
                "AutoSnapshotConfig",
                safeConfig.IsEnabled,
                automaticDescription,
                systemTimeZone.Id,
                BusinessScheduleCalculator.CalculateAutomaticNextRunUtc(safeConfig, utcNow, systemTimeZone)),
            new BusinessScheduleDescriptor(
                ScheduledJobKey.StockPriceUpdate,
                "目前股價更新",
                "固定市場排程",
                true,
                "台灣平日 23:00",
                BusinessScheduleCalculator.TaiwanTimeZone.Id,
                BusinessScheduleCalculator.CalculateStockPriceNextRunUtc(utcNow)),
            new BusinessScheduleDescriptor(
                ScheduledJobKey.HistoricalMarketDataSync,
                "歷史行情同步",
                "固定市場排程",
                true,
                "台灣平日 23:30",
                BusinessScheduleCalculator.TaiwanTimeZone.Id,
                BusinessScheduleCalculator.CalculateHistoricalSyncNextRunUtc(utcNow)),
        ];
    }

    /// <summary>建立自動快照設定的 bounded 顯示規則。</summary>
    private static string BuildAutomaticDescription(AutoSnapshotConfig config)
        => config.Frequency switch
        {
            "Daily" => $"每日 {config.TimeOfDay}",
            "Weekly" => $"每週 {config.DayOfWeek?.ToString() ?? "未設定"} {config.TimeOfDay}",
            "Monthly" => $"每月 {config.DayOfMonth?.ToString() ?? "未設定"} 日 {config.TimeOfDay}",
            _ => "設定無效",
        };
}
