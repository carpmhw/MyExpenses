using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class AutoSnapshotScheduleValidatorTests
{
    /// <summary>驗證合法 Daily 設定可通過且不依賴星期或月份欄位。</summary>
    [Fact]
    public void Validate_DailyConfigurationAcceptsStrictTime()
    {
        var result = AutoSnapshotScheduleValidator.Validate(new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Daily",
            TimeOfDay = "08:05",
        });

        Assert.True(result.IsValid);
    }

    /// <summary>驗證未知 frequency、錯誤時間與範圍外日期會被拒絕。</summary>
    [Fact]
    public void Validate_RejectsInvalidFrequencyTimeAndCalendarValues()
    {
        var result = AutoSnapshotScheduleValidator.Validate(new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Hourly",
            TimeOfDay = "8:5",
            DayOfWeek = 7,
            DayOfMonth = 32,
        });

        Assert.False(result.IsValid);
        Assert.Contains("Frequency", result.Errors.Keys);
        Assert.Contains("TimeOfDay", result.Errors.Keys);
        Assert.Contains("DayOfWeek", result.Errors.Keys);
        Assert.Contains("DayOfMonth", result.Errors.Keys);
    }

    /// <summary>驗證 Weekly 與 Monthly 必須提供各自有效的日曆欄位。</summary>
    [Fact]
    public void Validate_RequiresFrequencySpecificCalendarValue()
    {
        var weekly = AutoSnapshotScheduleValidator.Validate(new AutoSnapshotConfig
        {
            Frequency = "Weekly",
            TimeOfDay = "08:00",
        });
        var monthly = AutoSnapshotScheduleValidator.Validate(new AutoSnapshotConfig
        {
            Frequency = "Monthly",
            TimeOfDay = "08:00",
        });

        Assert.False(weekly.IsValid);
        Assert.Contains("DayOfWeek", weekly.Errors.Keys);
        Assert.False(monthly.IsValid);
        Assert.Contains("DayOfMonth", monthly.Errors.Keys);
    }
}
