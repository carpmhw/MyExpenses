using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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

public sealed class ScheduleSecurityIntegrationTests
{
    /// <summary>驗證匿名 client 查詢排程總覽會得到 401。</summary>
    [Fact]
    public async Task ScheduleOverview_RejectsAnonymousCaller()
    {
        await using var app = await CreateAppAsync();
        TestAuthHandler.Authenticate = false;

        var response = await app.App.GetTestClient().GetAsync("/api/schedules");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>驗證 authenticated browser owner 可查詢排程總覽。</summary>
    [Fact]
    public async Task ScheduleOverview_AllowsAuthenticatedBrowserOwner()
    {
        await using var app = await CreateAppAsync();

        var response = await app.App.GetTestClient().GetAsync("/api/schedules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>驗證未宣告 schedule scope 的 API token 依 default-deny 回傳 403。</summary>
    [Fact]
    public async Task ScheduleOverview_RejectsApiTokenByDefault()
    {
        await using var app = await CreateAppAsync();
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token);

        var response = await client.GetAsync("/api/schedules");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>建立含 SQLite、fake authentication 與 API token middleware 的測試 host。</summary>
    private static async Task<TestApp> CreateAppAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.Configure<TimeZoneOptions>(options => options.Default = "Asia/Taipei");
        builder.Services.AddSingleton<TimeZoneService>();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<ScheduledJobExecutionRepository>();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.AutoSnapshotConfigs.Add(new AutoSnapshotConfig
            {
                IsEnabled = false,
                Frequency = "Daily",
                TimeOfDay = "08:00",
            });
            var user = new User
            {
                Id = 1,
                Email = "schedule-owner@example.com",
                DisplayName = "Schedule Owner",
                PasswordHash = "unused",
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var token = "oc_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ApiTokens.Add(new ApiToken
            {
                UserId = 1,
                Name = "schedule-test-token",
                TokenHash = BCrypt.Net.BCrypt.HashPassword(token),
                Prefix = token[..12],
                Scopes = null,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        app.UseAuthentication();
        app.UseMiddleware<ApiTokenAuthMiddleware>();
        app.UseMiddleware<ApiTokenScopeMiddleware>();
        app.UseAuthorization();
        app.MapScheduleEndpoints();
        await app.StartAsync();
        TestAuthHandler.Authenticate = true;
        return new TestApp(app, connection, token);
    }

    /// <summary>保存測試 host、SQLite connection 與 API token。</summary>
    private sealed record TestApp(WebApplication App, SqliteConnection Connection, string Token) : IAsyncDisposable
    {
        /// <summary>釋放測試 host 並重設 fake authentication 狀態。</summary>
        public async ValueTask DisposeAsync()
        {
            TestAuthHandler.Authenticate = true;
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>提供可切換 authenticated browser owner 的測試 authentication handler。</summary>
    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>取得測試 authentication scheme name。</summary>
        public const string SchemeName = "ScheduleTest";

        /// <summary>控制目前 request 是否視為 browser owner。</summary>
        public static bool Authenticate { get; set; } = true;

        /// <summary>依測試狀態建立 owner principal 或匿名結果。</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Authenticate)
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "1")],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
