using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class TimeZoneServiceTests
{
    /// <summary>Verifies the initial time zone prefers TZ over configured defaults and the built-in fallback.</summary>
    [Fact]
    public void ResolveInitialTimeZoneId_UsesEnvironmentThenOptionsThenFallback()
    {
        Assert.Equal("America/New_York", TimeZoneService.ResolveInitialTimeZoneId("America/New_York", "Europe/London"));
        Assert.Equal("Europe/London", TimeZoneService.ResolveInitialTimeZoneId(null, "Europe/London"));
        Assert.Equal("Asia/Taipei", TimeZoneService.ResolveInitialTimeZoneId(null, null));
    }

    /// <summary>Verifies a persisted system setting remains authoritative over deployment configuration.</summary>
    [Fact]
    public async Task InitializeAsync_PrefersPersistedTimeZone()
    {
        await using var db = await CreateDbContextAsync();
        db.SystemSettings.Add(new SystemSetting { Id = 1, TimeZoneId = "America/New_York" });
        await db.SaveChangesAsync();

        var service = CreateService("Asia/Taipei", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        await service.InitializeAsync(db);

        Assert.Equal("America/New_York", service.TimeZoneId);
    }

    /// <summary>Verifies a valid persisted value can start even when deployment configuration is invalid.</summary>
    [Fact]
    public async Task InitializeAsync_UsesPersistedValueBeforeValidatingInvalidConfiguration()
    {
        await using var db = await CreateDbContextAsync();
        db.SystemSettings.Add(new SystemSetting { Id = 1, TimeZoneId = "America/New_York" });
        await db.SaveChangesAsync();

        var service = CreateService("Not/A-TimeZone", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));

        await service.InitializeAsync(db);

        Assert.Equal("America/New_York", service.TimeZoneId);
    }

    /// <summary>Verifies first initialization persists the configured time zone when no setting exists.</summary>
    [Fact]
    public async Task InitializeAsync_PersistsConfiguredTimeZoneWhenMissing()
    {
        await using var db = await CreateDbContextAsync();
        var service = CreateService("Europe/London", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));

        await service.InitializeAsync(db);

        var setting = await db.SystemSettings.SingleAsync();
        Assert.Equal("Europe/London", setting.TimeZoneId);
        Assert.Equal("Europe/London", service.TimeZoneId);

        var restartedService = CreateService("Asia/Taipei", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        await restartedService.InitializeAsync(db);

        Assert.Equal("Europe/London", restartedService.TimeZoneId);
    }

    /// <summary>Verifies an invalid deployment time zone falls back before creating a new persisted setting.</summary>
    [Fact]
    public async Task InitializeAsync_FallsBackWhenConfiguredTimeZoneIsInvalid()
    {
        await using var db = await CreateDbContextAsync();
        var service = CreateService("Not/A-TimeZone", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));

        await service.InitializeAsync(db);

        Assert.Equal("Asia/Taipei", service.TimeZoneId);
        Assert.Equal("Asia/Taipei", (await db.SystemSettings.SingleAsync()).TimeZoneId);
    }

    /// <summary>Verifies invalid time zones are rejected without changing the cached value.</summary>
    [Fact]
    public async Task UpdateAsync_RejectsInvalidTimeZoneAndRetainsCurrentValue()
    {
        await using var db = await CreateDbContextAsync();
        var service = CreateService("Asia/Taipei", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        await service.InitializeAsync(db);

        var updated = await service.UpdateAsync(db, "Not/A-TimeZone");

        Assert.False(updated);
        Assert.Equal("Asia/Taipei", service.TimeZoneId);
        Assert.Equal("Asia/Taipei", (await db.SystemSettings.SingleAsync()).TimeZoneId);
    }

    /// <summary>Verifies current date derives from the configured zone rather than UTC.</summary>
    [Fact]
    public void GetLocalDate_UsesConfiguredTimeZone()
    {
        var service = CreateService("Asia/Taipei", new DateTime(2026, 7, 14, 16, 30, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 7, 15), service.GetLocalDate());
    }

    /// <summary>Verifies local date boundaries convert to a UTC half-open interval across offsets.</summary>
    [Theory]
    [InlineData("Asia/Taipei", 2026, 7, 15, "2026-07-14T16:00:00.0000000Z", "2026-07-15T16:00:00.0000000Z")]
    [InlineData("America/New_York", 2026, 7, 15, "2026-07-15T04:00:00.0000000Z", "2026-07-16T04:00:00.0000000Z")]
    public void ConvertLocalDateRangeToUtc_UsesHalfOpenBoundaries(
        string timeZoneId,
        int year,
        int month,
        int day,
        string expectedStart,
        string expectedEnd)
    {
        var service = CreateService(timeZoneId, new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));

        var range = service.ConvertLocalDateRangeToUtc(new DateOnly(year, month, day), new DateOnly(year, month, day));

        Assert.Equal(DateTime.Parse(expectedStart).ToUniversalTime(), range.StartUtc);
        Assert.Equal(DateTime.Parse(expectedEnd).ToUniversalTime(), range.EndExclusiveUtc);
    }

    /// <summary>Creates a time zone service backed by a deterministic UTC clock.</summary>
    private static TimeZoneService CreateService(string timeZoneId, DateTime utcNow)
    {
        return new TimeZoneService(
            Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = timeZoneId }),
            new FixedTimeProvider(utcNow));
    }

    /// <summary>Creates a SQLite context for time zone persistence tests.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    /// <summary>Provides a deterministic UTC instant to services under test.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        /// <summary>Initializes the provider with a fixed UTC instant.</summary>
        public FixedTimeProvider(DateTime utcNow)
        {
            _utcNow = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        }

        /// <summary>Returns the configured fixed UTC instant.</summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
