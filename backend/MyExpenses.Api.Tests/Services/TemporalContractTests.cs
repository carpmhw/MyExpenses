using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class TemporalContractTests
{
    /// <summary>Verifies installment paid dates use DateOnly without an implicit time zone.</summary>
    [Fact]
    public void InstallmentPayment_PaidDateIsDateOnly()
    {
        var property = typeof(InstallmentPayment).GetProperty(nameof(InstallmentPayment.PaidDate));

        Assert.Equal(typeof(DateOnly?), property!.PropertyType);
    }

    /// <summary>Verifies SQLite round trips restore persisted event timestamps as UTC values.</summary>
    [Fact]
    public async Task SqliteRoundTrip_RestoresEventTimestampsAsUtc()
    {
        await using var db = await CreateDbContextAsync();
        var createdAt = new DateTime(2026, 7, 15, 1, 2, 3, DateTimeKind.Utc);
        var user = new User
        {
            Email = "utc@example.com",
            DisplayName = "UTC",
            PasswordHash = "hash",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await db.Users.SingleAsync();

        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAt.Kind);
    }

    /// <summary>Verifies the standard JSON timestamp contract emits UTC instants with a Z suffix.</summary>
    [Fact]
    public void JsonTimestamp_UsesExplicitUtcSuffix()
    {
        var timestamp = new DateTime(2026, 7, 15, 1, 2, 3, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(new { createdAt = timestamp });

        Assert.Contains("2026-07-15T01:02:03Z", json);
    }

    /// <summary>Verifies the server no longer contains the timestamp-localizing JSON converter.</summary>
    [Fact]
    public void Server_DoesNotContainLocalizingTimestampConverter()
    {
        var converter = typeof(TimeZoneService).Assembly.GetType("MyExpenses.Api.Converters.UtcToLocalDateTimeConverter");

        Assert.Null(converter);
    }

    /// <summary>Creates a SQLite context for temporal persistence tests.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
