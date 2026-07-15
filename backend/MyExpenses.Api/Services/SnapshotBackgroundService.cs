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

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>Checks the current system-local schedule once and creates an automatic snapshot when due.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await db.AutoSnapshotConfigs.FirstOrDefaultAsync(cancellationToken);
        if (config is null || !config.IsEnabled)
            return;

        var nowUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        if (!IsScheduleDue(config, nowUtc, config.LastRunAt, _timeZoneService.GetTimeZoneInfo()))
            return;

        var localNow = _timeZoneService.ConvertUtcToLocal(nowUtc);
        var bankAccounts = await db.BankAccounts.ToListAsync(cancellationToken);
        var stocks = await db.Stocks.ToListAsync(cancellationToken);
        var snapshot = FinancialSnapshotBuilder.Build(
            BuildAutomaticSnapshotName(localNow),
            "系統自動建立",
            nowUtc,
            bankAccounts,
            stocks);

        db.SnapshotBatches.Add(snapshot);
        config.LastRunAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Auto snapshot created: {Id} at {Date}", snapshot.Id, nowUtc);
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

        return config.Frequency switch
        {
            "Daily" => true,
            "Weekly" => config.DayOfWeek.HasValue && (int)localNow.DayOfWeek == config.DayOfWeek.Value,
            "Monthly" => config.DayOfMonth.HasValue && localNow.Day == config.DayOfMonth.Value,
            _ => false,
        };
    }

    /// <summary>Builds the generated name for an automatic snapshot from local schedule time.</summary>
    public static string BuildAutomaticSnapshotName(DateTime localNow)
        => $"自動快照 {localNow:yyyy-MM-dd HH:mm}";
}
