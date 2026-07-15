using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class SnapshotTimeZoneTests
{
    /// <summary>Verifies positive-offset local-day filters use the configured UTC boundaries.</summary>
    [Fact]
    public async Task ListSnapshots_UsesPositiveOffsetLocalDayBoundaries()
    {
        await using var db = await CreateDbContextAsync(
            new DateTime(2026, 7, 14, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc));
        var service = CreateService("Asia/Taipei");

        var result = await SnapshotEndpoints.ListSnapshotsAsync(
            1, 20, new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 15), db, service);

        Assert.Equal(new[] { "end", "start" }, result.Items.Select(item => item.Name).ToArray());
    }

    /// <summary>Verifies negative-offset local-day filters exclude instants before local midnight.</summary>
    [Fact]
    public async Task ListSnapshots_UsesNegativeOffsetLocalDayBoundaries()
    {
        await using var db = await CreateDbContextAsync(
            new DateTime(2026, 7, 15, 4, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 16, 4, 0, 0, DateTimeKind.Utc));
        var service = CreateService("America/New_York");

        var result = await SnapshotEndpoints.ListSnapshotsAsync(
            1, 20, new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 15), db, service);

        Assert.Equal(new[] { "end", "start" }, result.Items.Select(item => item.Name).ToArray());
    }

    /// <summary>Verifies daylight-saving fall-back days use different UTC offsets at each boundary.</summary>
    [Fact]
    public async Task ListSnapshots_UsesDstFallBackBoundaryLength()
    {
        await using var db = await CreateDbContextAsync(
            new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc));
        var service = CreateService("America/New_York");

        var result = await SnapshotEndpoints.ListSnapshotsAsync(
            1, 20, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), db, service);

        Assert.Equal(2, result.Total);
    }

    /// <summary>Creates a SQLite context containing snapshots at both local-day boundaries.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync(DateTime firstUtc, DateTime secondUtc)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.SnapshotBatches.AddRange(
            new SnapshotBatch { Name = "start", SnapshotDate = firstUtc, TotalNetWorth = 1m },
            new SnapshotBatch { Name = "end", SnapshotDate = secondUtc.AddTicks(-1), TotalNetWorth = 2m },
            new SnapshotBatch { Name = "outside", SnapshotDate = firstUtc.AddTicks(-1), TotalNetWorth = 3m });
        await db.SaveChangesAsync();
        return db;
    }

    /// <summary>Creates a time zone service for snapshot boundary tests.</summary>
    private static TimeZoneService CreateService(string timeZoneId)
    {
        return new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { Default = timeZoneId }));
    }
}
