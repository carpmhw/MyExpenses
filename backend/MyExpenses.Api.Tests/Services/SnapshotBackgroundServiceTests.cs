using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
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

    /// <summary>驗證每月 29 至 31 日在短月份會於當月最後一天到期。</summary>
    [Fact]
    public void IsScheduleDue_MonthlyDayThirtyOneClampsToShortMonthEnd()
    {
        var config = new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Monthly",
            DayOfMonth = 31,
            TimeOfDay = "08:00",
        };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

        Assert.False(SnapshotBackgroundService.IsScheduleDue(
            config,
            new DateTime(2026, 2, 27, 0, 1, 0, DateTimeKind.Utc),
            null,
            zone));
        Assert.True(SnapshotBackgroundService.IsScheduleDue(
            config,
            new DateTime(2026, 2, 28, 0, 1, 0, DateTimeKind.Utc),
            null,
            zone));
    }

    /// <summary>驗證服務首次啟動檢查會跳過停機期間已錯過的到期時槽。</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ShouldSkipInitialCheck_DoesNotCatchUpMissedRun(
        bool isInitialCheck,
        bool isDue,
        bool expected)
    {
        Assert.Equal(expected, SnapshotBackgroundService.ShouldSkipInitialCheck(isInitialCheck, isDue));
    }

    /// <summary>驗證首次跳過 missed slot 後的下一次檢查仍不會建立快照。</summary>
    [Fact]
    public async Task RunOnceAsync_DoesNotCatchUpOnSecondCheckAfterStartup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
        {
            IsEnabled = true,
            Frequency = "Daily",
            TimeOfDay = "08:00",
        });
        await db.SaveChangesAsync();

        var fixedTime = new FixedTimeProvider(new DateTime(2026, 8, 8, 0, 1, 0, DateTimeKind.Utc));
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddScoped<ScheduledJobExecutionRepository>();
        services.AddScoped<ScheduledJobRunner>(_ => new ScheduledJobRunner(
            new ScheduledJobExecutionRepository(db),
            NullLogger<ScheduledJobRunner>.Instance,
            fixedTime,
            new ScheduledJobRunnerOptions { RetryDelay = TimeSpan.Zero }));
        services.AddScoped<AutomaticSnapshotWorkflow>(_ => new AutomaticSnapshotWorkflow(db, fixedTime));
        await using var serviceProvider = services.BuildServiceProvider();
        var timeZoneService = new TimeZoneService(
            Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = "Asia/Taipei" }),
            fixedTime);
        var service = new SnapshotBackgroundService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SnapshotBackgroundService>.Instance,
            timeZoneService,
            fixedTime);

        await service.RunOnceAsync();
        await service.RunOnceAsync();

        Assert.Empty(await db.SnapshotBatches.ToListAsync());
        Assert.Empty(await db.ScheduledJobExecutions.ToListAsync());
    }

    /// <summary>Verifies automatic snapshot names use the system-local scheduled date and time.</summary>
    [Fact]
    public void BuildAutomaticSnapshotName_UsesLocalScheduleSemantics()
    {
        var name = SnapshotBackgroundService.BuildAutomaticSnapshotName(
            new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Unspecified));

        Assert.Equal("自動快照 2026-07-15 08:00", name);
    }

    /// <summary>建立使用已開啟 SQLite 連線的測試資料庫。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>提供固定 UTC 時間供 hosted service 測試使用。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        /// <summary>初始化固定 UTC instant。</summary>
        public FixedTimeProvider(DateTime utcNow)
        {
            _utcNow = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        }

        /// <summary>回傳測試指定的 UTC instant。</summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
