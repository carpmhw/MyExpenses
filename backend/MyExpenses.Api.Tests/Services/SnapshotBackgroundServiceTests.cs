using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class SnapshotBackgroundServiceTests
{
    /// <summary>Verifies daily schedules trigger after the configured local wall time.</summary>
    [Fact]
    public void IsScheduleDue_DailyScheduleUsesLocalTime()
    {
        var config = new AutoSnapshotConfig { IsEnabled = true, Frequency = "Daily", TimeOfDay = "08:00" };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

        Assert.True(SnapshotBackgroundService.IsScheduleDue(
            config,
            new DateTime(2026, 7, 14, 0, 1, 0, DateTimeKind.Utc),
            null,
            zone));
    }

    /// <summary>Verifies weekly and monthly schedules match local calendar fields rather than UTC fields.</summary>
    [Fact]
    public void IsScheduleDue_WeeklyAndMonthlyUseLocalCalendar()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        var utcNow = new DateTime(2026, 7, 14, 0, 1, 0, DateTimeKind.Utc);
        var weekly = new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Weekly",
            DayOfWeek = (int)DayOfWeek.Tuesday,
            TimeOfDay = "08:00",
        };
        var monthly = new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Monthly",
            DayOfMonth = 14,
            TimeOfDay = "08:00",
        };

        Assert.True(SnapshotBackgroundService.IsScheduleDue(weekly, utcNow, null, zone));
        Assert.True(SnapshotBackgroundService.IsScheduleDue(monthly, utcNow, null, zone));
    }

    /// <summary>Verifies repeated local times are blocked after a successful run on the same local date.</summary>
    [Fact]
    public void IsScheduleDue_UsesLocalDateForDuplicateGuard()
    {
        var config = new AutoSnapshotConfig { IsEnabled = true, Frequency = "Daily", TimeOfDay = "01:30" };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var utcNow = new DateTime(2026, 11, 1, 6, 45, 0, DateTimeKind.Utc);
        var lastRun = new DateTime(2026, 11, 1, 5, 45, 0, DateTimeKind.Utc);

        Assert.False(SnapshotBackgroundService.IsScheduleDue(config, utcNow, lastRun, zone));
    }

    /// <summary>Verifies a nonexistent local wall-clock time runs on the first check after the requested time.</summary>
    [Fact]
    public void IsScheduleDue_DstGapRunsAfterRequestedWallTime()
    {
        var config = new AutoSnapshotConfig { IsEnabled = true, Frequency = "Daily", TimeOfDay = "02:30" };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.True(SnapshotBackgroundService.IsScheduleDue(
            config,
            new DateTime(2026, 3, 8, 7, 5, 0, DateTimeKind.Utc),
            null,
            zone));
    }

    /// <summary>Verifies automatic snapshot names use the system-local scheduled date and time.</summary>
    [Fact]
    public void BuildAutomaticSnapshotName_UsesLocalScheduleSemantics()
    {
        var name = SnapshotBackgroundService.BuildAutomaticSnapshotName(
            new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Unspecified));

        Assert.Equal("自動快照 2026-07-15 08:00", name);
    }
}
