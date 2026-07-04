using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
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
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class AuthSecurityEndpointsTests
{
    /// <summary>Verifies registration rejects passwords that do not meet the shared password policy.</summary>
    [Fact]
    public async Task Register_RejectsWeakPassword()
    {
        await using var app = await CreateAuthAppAsync();
        var client = app.App.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "weak-register@example.com",
            displayName = "Weak Register",
            password = "short1!",
        });

        await AssertStatusCodeAsync(HttpStatusCode.BadRequest, response);
    }

    /// <summary>Verifies password changes reject new passwords that do not meet the shared password policy.</summary>
    [Fact]
    public async Task ChangePassword_RejectsWeakNewPassword()
    {
        await using var app = await CreateAuthAppAsync();
        await SeedUserAsync(app.App);
        var client = app.App.GetTestClient();

        var response = await client.PutAsJsonAsync("/api/auth/password", new
        {
            currentPassword = StrongPassword,
            newPassword = "short1!",
        });

        await AssertStatusCodeAsync(HttpStatusCode.BadRequest, response);
    }

    /// <summary>Verifies sensitive public authentication endpoints reject excess attempts with rate limiting.</summary>
    [Theory]
    [InlineData("/api/auth/login", "login")]
    [InlineData("/api/auth/2fa/login", "2fa")]
    [InlineData("/api/auth/2fa/recovery-login", "recovery")]
    public async Task SensitivePublicAuthEndpoints_RateLimitRepeatedFailures(string path, string requestKind)
    {
        await using var app = await CreateAuthAppAsync(useRateLimiter: true);
        var client = app.App.GetTestClient();

        HttpResponseMessage response = new(HttpStatusCode.OK);
        for (var attempt = 0; attempt <= AuthRateLimitPolicy.PermitLimit; attempt++)
        {
            response = await PostInvalidAuthAttemptAsync(client, path, requestKind);
        }

        await AssertStatusCodeAsync(HttpStatusCode.TooManyRequests, response);
    }

    /// <summary>Creates an invalid request body for the selected authentication endpoint.</summary>
    private static Task<HttpResponseMessage> PostInvalidAuthAttemptAsync(HttpClient client, string path, string requestKind)
    {
        return requestKind switch
        {
            "login" => client.PostAsJsonAsync(path, new { email = "missing@example.com", password = "wrong-password" }),
            "2fa" => client.PostAsJsonAsync(path, new { tempToken = "invalid-temp-token", code = "000000" }),
            "recovery" => client.PostAsJsonAsync(path, new { tempToken = "invalid-temp-token", recoveryCode = "BAD-CODE" }),
            _ => throw new ArgumentOutOfRangeException(nameof(requestKind), requestKind, "Unknown auth request kind."),
        };
    }

    /// <summary>Creates a minimal auth test application with SQLite, fake authentication, and optional rate limiting.</summary>
    private static async Task<TestApp> CreateAuthAppAsync(bool useRateLimiter = false)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddDataProtection();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        if (useRateLimiter)
        {
            builder.Services.AddRateLimiter(AuthRateLimitPolicy.Configure);
        }

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        if (useRateLimiter)
        {
            app.UseRateLimiter();
        }
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAuthEndpoints();
        await app.StartAsync();

        return new TestApp(app, connection);
    }

    /// <summary>Seeds one authenticated test user for protected auth endpoint tests.</summary>
    private static async Task SeedUserAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User
        {
            Id = TestAuthHandler.UserId,
            Email = "auth-security@example.com",
            DisplayName = "Auth Security",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(StrongPassword),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Asserts a response status code while including the response body in failure output.</summary>
    private static async Task AssertStatusCodeAsync(HttpStatusCode expectedStatusCode, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expectedStatusCode,
            $"Expected {(int)expectedStatusCode} {expectedStatusCode}, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
    }

    private const string StrongPassword = "Valid!Password123";

    private sealed record TestApp(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        /// <summary>Disposes the test host and SQLite connection.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";
        public const int UserId = 1;

        /// <summary>Creates a fake authentication handler that always authenticates the seeded test user.</summary>
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        /// <summary>Authenticates requests as the seeded test user.</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Email, "auth-security@example.com"),
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
