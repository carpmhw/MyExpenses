using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;

namespace MyExpenses.Api.Services;

/// <summary>Provides the persisted system time zone and shared UTC/local calendar operations.</summary>
public class TimeZoneService
{
    private readonly TimeProvider _timeProvider;
    private readonly string _configuredTimeZoneId;
    private TimeZoneInfo _timeZone;

    /// <summary>Initializes the provider from deployment configuration until persistence is loaded.</summary>
    public TimeZoneService(IOptions<TimeZoneOptions> options, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var configuredTimeZoneId = ResolveInitialTimeZoneId(
            Environment.GetEnvironmentVariable("TZ"),
            options.Value.Default);
        if (!TryFindTimeZone(configuredTimeZoneId, out var configuredTimeZone))
        {
            configuredTimeZoneId = "Asia/Taipei";
            configuredTimeZone = FindTimeZone(configuredTimeZoneId);
        }

        _configuredTimeZoneId = configuredTimeZoneId;
        _timeZone = configuredTimeZone;
    }

    /// <summary>Gets the currently cached IANA system time zone identifier.</summary>
    public string TimeZoneId => Volatile.Read(ref _timeZone).Id;

    /// <summary>Resolves the initial identifier using environment, configuration, and the built-in fallback.</summary>
    public static string ResolveInitialTimeZoneId(string? environmentTimeZoneId, string? configuredTimeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(environmentTimeZoneId))
            return environmentTimeZoneId.Trim();
        if (!string.IsNullOrWhiteSpace(configuredTimeZoneId))
            return configuredTimeZoneId.Trim();
        return "Asia/Taipei";
    }

    /// <summary>Loads the persisted singleton setting or creates it from deployment configuration.</summary>
    public async Task InitializeAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var setting = await db.SystemSettings
            .SingleOrDefaultAsync(item => item.Id == SystemSetting.SingletonId, cancellationToken);

        if (setting is null)
        {
            var initialTimeZone = FindTimeZone(_configuredTimeZoneId);
            setting = new SystemSetting
            {
                Id = SystemSetting.SingletonId,
                TimeZoneId = initialTimeZone.Id,
            };
            db.SystemSettings.Add(setting);
            await db.SaveChangesAsync(cancellationToken);
            Volatile.Write(ref _timeZone, initialTimeZone);
            return;
        }

        var persistedTimeZone = FindTimeZone(setting.TimeZoneId);
        Volatile.Write(ref _timeZone, persistedTimeZone);
    }

    /// <summary>Persists a valid time zone and updates the runtime cache only after persistence succeeds.</summary>
    public async Task<bool> UpdateAsync(
        AppDbContext db,
        string? timeZoneId,
        CancellationToken cancellationToken = default)
    {
        if (!TryFindTimeZone(timeZoneId, out var updatedTimeZone))
            return false;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var setting = await db.SystemSettings
            .SingleOrDefaultAsync(item => item.Id == SystemSetting.SingletonId, cancellationToken);
        if (setting is null)
        {
            setting = new SystemSetting { Id = SystemSetting.SingletonId };
            db.SystemSettings.Add(setting);
        }

        setting.TimeZoneId = updatedTimeZone.Id;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Volatile.Write(ref _timeZone, updatedTimeZone);
        return true;
    }

    /// <summary>Gets the currently cached time zone information.</summary>
    public TimeZoneInfo GetTimeZoneInfo() => Volatile.Read(ref _timeZone);

    /// <summary>Converts a UTC instant to the current system-local wall-clock time.</summary>
    public DateTime ConvertUtcToLocal(DateTime utcDateTime)
    {
        if (utcDateTime.Kind == DateTimeKind.Local)
            utcDateTime = utcDateTime.ToUniversalTime();
        if (utcDateTime.Kind == DateTimeKind.Unspecified)
            utcDateTime = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, GetTimeZoneInfo());
    }

    /// <summary>Converts a system-local wall-clock time to a UTC instant.</summary>
    public DateTime ConvertLocalToUtc(DateTime localDateTime)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, GetTimeZoneInfo());
    }

    /// <summary>Gets the current date in the configured system time zone.</summary>
    public DateOnly GetLocalDate() => DateOnly.FromDateTime(GetLocalNow());

    /// <summary>Gets the current local wall-clock time in the configured system time zone.</summary>
    public DateTime GetLocalNow() => ConvertUtcToLocal(_timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>Converts inclusive local dates into a half-open UTC interval.</summary>
    public UtcDateTimeRange ConvertLocalDateRangeToUtc(DateOnly startDate, DateOnly endDate)
    {
        if (endDate < startDate)
            throw new ArgumentException("End date cannot be earlier than start date.", nameof(endDate));

        var startLocal = startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endLocal = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new UtcDateTimeRange(
            ConvertLocalToUtc(startLocal),
            ConvertLocalToUtc(endLocal));
    }

    /// <summary>Resolves a supported time zone identifier or throws for invalid persisted configuration.</summary>
    private static TimeZoneInfo FindTimeZone(string timeZoneId)
    {
        if (!TryFindTimeZone(timeZoneId, out var timeZone))
            throw new InvalidOperationException($"Unsupported system time zone: {timeZoneId}");
        return timeZone;
    }

    /// <summary>Tries to resolve a supported IANA time zone identifier.</summary>
    private static bool TryFindTimeZone(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            timeZone = null!;
            return false;
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = null!;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = null!;
            return false;
        }
        catch (ArgumentException)
        {
            timeZone = null!;
            return false;
        }
    }
}
