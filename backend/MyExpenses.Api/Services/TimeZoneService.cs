using Microsoft.Extensions.Options;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>
/// 時區服務，提供 UTC 與設定時區之間的轉換
/// </summary>
public class TimeZoneService
{
    private readonly TimeZoneInfo _timeZone;

    /// <summary>
    /// 初始化時區服務
    /// </summary>
    /// <param name="options">時區設定選項</param>
    public TimeZoneService(IOptions<TimeZoneOptions> options)
    {
        var tzId = Environment.GetEnvironmentVariable("TZ")
                   ?? options.Value.Default
                   ?? "Asia/Taipei";
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
    }

    /// <summary>取得設定的時區</summary>
    public TimeZoneInfo GetTimeZoneInfo() => _timeZone;

    /// <summary>將 UTC 時間轉為設定時區的當地時間</summary>
    public DateTime ConvertUtcToLocal(DateTime utcDateTime)
    {
        if (utcDateTime.Kind == DateTimeKind.Local)
            utcDateTime = utcDateTime.ToUniversalTime();
        if (utcDateTime.Kind == DateTimeKind.Unspecified)
            utcDateTime = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, _timeZone);
    }

    /// <summary>取得設定時區的當前時間</summary>
    public DateTime GetLocalNow()
        => ConvertUtcToLocal(DateTime.UtcNow);
}
