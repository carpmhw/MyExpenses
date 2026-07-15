using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class SettingsEndpointsTests
{
    /// <summary>Verifies unauthenticated callers cannot read the system time zone.</summary>
    [Fact]
    public async Task GetTimeZone_RequiresAuthentication()
    {
        await using var app = await CreateAppAsync(authenticated: false);

        var response = await app.App.GetTestClient().GetAsync("/api/settings/timezone");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Verifies an authenticated caller can read and update the persisted system time zone.</summary>
    [Fact]
    public async Task TimeZoneEndpoints_ReadUpdateAndRetainValue()
    {
        await using var app = await CreateAppAsync(authenticated: true);
        var client = app.App.GetTestClient();

        var initial = await client.GetFromJsonAsync<TimeZoneResponse>("/api/settings/timezone");
        Assert.Equal("Asia/Taipei", initial!.TimeZoneId);

        var update = await client.PutAsJsonAsync("/api/settings/timezone", new { timeZoneId = "America/New_York" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("America/New_York", (await update.Content.ReadFromJsonAsync<TimeZoneResponse>())!.TimeZoneId);

        var persisted = await client.GetFromJsonAsync<TimeZoneResponse>("/api/settings/timezone");
        Assert.Equal("America/New_York", persisted!.TimeZoneId);
    }

    /// <summary>Verifies invalid time zone updates are rejected without changing the persisted value.</summary>
    [Fact]
    public async Task UpdateTimeZone_RejectsInvalidValueAndRetainsPreviousValue()
    {
        await using var app = await CreateAppAsync(authenticated: true);
        var client = app.App.GetTestClient();

        var response = await client.PutAsJsonAsync("/api/settings/timezone", new { timeZoneId = "Not/A-TimeZone" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var current = await client.GetFromJsonAsync<TimeZoneResponse>("/api/settings/timezone");
        Assert.Equal("Asia/Taipei", current!.TimeZoneId);
    }

    /// <summary>Creates a settings test host with SQLite, persisted settings, and optional fake authentication.</summary>
    private static async Task<TestApp> CreateAppAsync(bool authenticated)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.Configure<TimeZoneOptions>(options => options.Default = "Asia/Taipei");
        builder.Services.AddSingleton<TimeZoneService>();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.SystemSettings.Add(new SystemSetting { Id = 1, TimeZoneId = "Asia/Taipei" });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<TimeZoneService>().InitializeAsync(db);
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSettingsEndpoints();
        await app.StartAsync();

        if (!authenticated)
            TestAuthHandler.Authenticate = false;

        return new TestApp(app, connection);
    }

    /// <summary>Represents the settings endpoint response contract.</summary>
    private sealed record TimeZoneResponse(string TimeZoneId);

    /// <summary>Disposes the test host and its SQLite connection.</summary>
    private sealed record TestApp(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        /// <summary>Disposes test resources.</summary>
        public async ValueTask DisposeAsync()
        {
            TestAuthHandler.Authenticate = true;
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>Provides deterministic authentication for protected settings endpoint tests.</summary>
    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "SettingsTest";
        public static bool Authenticate { get; set; } = true;

        /// <summary>Authenticates the request when the test has enabled authentication.</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Authenticate)
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "1")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
