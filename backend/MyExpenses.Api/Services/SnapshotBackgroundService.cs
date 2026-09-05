using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public class SnapshotBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SnapshotBackgroundService> _logger;
    private readonly TimeZoneService _timeZoneService;
    private readonly TimeProvider _timeProvider;
    private int _initialCheckCompleted;
    private DateTime? _initialSkippedSlotUtc;

    /// <summary>Initializes the automatic snapshot service with shared time-zone and clock providers.</summary>
    public SnapshotBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<SnapshotBackgroundService> logger,
        TimeZoneService timeZoneService,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeZoneService = timeZoneService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs the periodic snapshot schedule loop until the host shuts down.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Snapshot background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error checking snapshot schedule");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), _timeProvider, stoppingToken);
        }
    }

    /// <summary>Checks the current system-local schedule once and creates an automatic snapshot when due.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await db.AutoSnapshotConfigs.SingleOrDefaultAsync(cancellationToken);
        var isInitialCheck = Interlocked.Exchange(ref _initialCheckCompleted, 1) == 0;
        if (config is null || !config.IsEnabled)
            return;

        var nowUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        var isDue = IsScheduleDue(config, nowUtc, config.LastRunAt, _timeZoneService.GetTimeZoneInfo());
        if (!isDue)
            return;

        var scheduledForUtc = BusinessScheduleCalculator.CalculateDueAutomaticSlotUtc(
            config,
            nowUtc,
            _timeZoneService.GetTimeZoneInfo());
        if (!scheduledForUtc.HasValue)
            return;

        if (ShouldSkipInitialCheck(isInitialCheck, isDue))
        {
            _initialSkippedSlotUtc = scheduledForUtc.Value;
            return;
        }

        if (_initialSkippedSlotUtc == scheduledForUtc.Value)
            return;

        _initialSkippedSlotUtc = null;

        var localScheduledDate = DateOnly.FromDateTime(
            _timeZoneService.ConvertUtcToLocal(scheduledForUtc.Value));
        var runner = scope.ServiceProvider.GetRequiredService<ScheduledJobRunner>();
        var workflow = scope.ServiceProvider.GetRequiredService<AutomaticSnapshotWorkflow>();
        await runner.RunAsync(
            ScheduledJobKey.AutomaticSnapshot,
            scheduledForUtc.Value,
            _timeZoneService.TimeZoneId,
            localScheduledDate,
            (_, token) => workflow.RunAsync(scheduledForUtc.Value, localScheduledDate, token),
            cancellationToken);
    }

    /// <summary>Determines whether a schedule is due using local date, weekday, month day, and wall-clock time.</summary>
    public static bool IsScheduleDue(
        AutoSnapshotConfig config,
        DateTime utcNow,
        DateTime? lastRunAtUtc,
        TimeZoneInfo timeZone)
    {
        if (!config.IsEnabled)
            return false;

        var normalizedUtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(normalizedUtcNow, timeZone);
        if (lastRunAtUtc.HasValue)
        {
            var normalizedLastRun = DateTime.SpecifyKind(lastRunAtUtc.Value, DateTimeKind.Utc);
            var lastRunLocal = TimeZoneInfo.ConvertTimeFromUtc(normalizedLastRun, timeZone);
            if (lastRunLocal.Date == localNow.Date)
                return false;
        }

        if (!TimeOnly.TryParseExact(
                config.TimeOfDay,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var scheduledTime))
            return false;

        if (TimeOnly.FromDateTime(localNow) < scheduledTime)
            return false;

        if (!BusinessScheduleCalculator.MatchesDate(config, localNow.Date))
            return false;

        var scheduledLocal = DateTime.SpecifyKind(
            localNow.Date.Add(scheduledTime.ToTimeSpan()),
            DateTimeKind.Unspecified);
        var scheduledUtc = BusinessScheduleCalculator.ResolveLocalDateTimeUtc(scheduledLocal, timeZone);
        return normalizedUtcNow >= scheduledUtc;
    }

    /// <summary>判斷首次 hosted service 檢查是否應跳過已錯過的排程時槽。</summary>
    public static bool ShouldSkipInitialCheck(bool isInitialCheck, bool isDue)
        => isInitialCheck && isDue;

    /// <summary>Builds the generated name for an automatic snapshot from local schedule time.</summary>
    public static string BuildAutomaticSnapshotName(DateTime localNow)
        => $"自動快照 {localNow:yyyy-MM-dd HH:mm}";
}
