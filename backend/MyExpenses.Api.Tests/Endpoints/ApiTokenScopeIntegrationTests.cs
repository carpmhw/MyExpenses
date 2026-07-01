using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class ApiTokenScopeIntegrationTests
{
    /// <summary>Verifies API tokens with transaction write scope can create transactions.</summary>
    [Fact]
    public async Task PostTransaction_AllowsApiTokenWithTransactionsWriteScope()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsWrite);
        var client = CreateAuthorizedClient(app);

        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            type = TransactionType.Expense,
            amount = 120m,
            description = "Lunch",
            categoryId = app.CategoryId,
        });

        await AssertStatusCodeAsync(HttpStatusCode.Created, response);
    }

    /// <summary>Verifies read-only transaction tokens cannot create transactions.</summary>
    [Fact]
    public async Task PostTransaction_RejectsApiTokenWithOnlyTransactionsReadScope()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsRead);
        var client = CreateAuthorizedClient(app);

        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            type = TransactionType.Expense,
            amount = 120m,
            description = "Lunch",
            categoryId = app.CategoryId,
        });

        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, response);
    }

    /// <summary>Verifies API tokens without scopes have no business API permissions.</summary>
    [Fact]
    public async Task GetTransactions_RejectsApiTokenWithNoScopes()
    {
        await using var app = await CreateAppAsync();
        var client = CreateAuthorizedClient(app);

        var response = await client.GetAsync("/api/transactions?limit=5");

        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, response);
    }

    /// <summary>Verifies API tokens cannot manage API tokens even when they have MCP scopes.</summary>
    [Fact]
    public async Task AuthApiTokens_RejectsApiTokenEvenWithMcpScopes()
    {
        await using var app = await CreateAppAsync(
            ApiTokenScopes.TransactionsRead,
            ApiTokenScopes.TransactionsWrite,
            ApiTokenScopes.TransactionsUndo,
            ApiTokenScopes.CategoriesRead,
            ApiTokenScopes.PaymentMethodsRead,
            ApiTokenScopes.ReportsRead);
        var client = CreateAuthorizedClient(app);

        var response = await client.PostAsJsonAsync("/api/auth/api-tokens", new
        {
            name = "nested token",
            scopes = new[] { ApiTokenScopes.TransactionsRead },
        });

        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, response);
    }

    /// <summary>Verifies unmarked business endpoints reject API tokens by default.</summary>
    [Fact]
    public async Task StockLookup_RejectsApiTokenBecauseEndpointIsUnmarked()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsRead);
        var client = CreateAuthorizedClient(app);

        var response = await client.GetAsync("/api/stocks/lookup?symbol=2330");

        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, response);
    }

    /// <summary>Verifies anonymous auth endpoints are not blocked by API token scope enforcement.</summary>
    [Fact]
    public async Task AuthStatus_AllowsAnonymousEndpointWithoutScopeMetadata()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsRead);
        var client = CreateAuthorizedClient(app);

        var response = await client.GetAsync("/api/auth/status");

        await AssertStatusCodeAsync(HttpStatusCode.OK, response);
    }

    /// <summary>Creates an authorized HTTP client for the supplied test app.</summary>
    private static HttpClient CreateAuthorizedClient(TestApp app)
    {
        var client = app.App.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.TokenValue);
        return client;
    }

    /// <summary>Asserts a response status code while including the response body in failure output.</summary>
    private static async Task AssertStatusCodeAsync(HttpStatusCode expectedStatusCode, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expectedStatusCode,
            $"Expected {(int)expectedStatusCode} {expectedStatusCode}, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
    }

    /// <summary>Creates a scoped test application with a single API token using the supplied scopes.</summary>
    private static async Task<TestApp> CreateAppAsync(params string[] scopes)
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
        builder.Services.AddHttpClient();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

        var app = builder.Build();
        app.UseMiddleware<ApiTokenAuthMiddleware>();
        app.UseMiddleware<ApiTokenScopeMiddleware>();
        app.MapTransactionEndpoints();
        app.MapAuthEndpoints();
        app.MapStockEndpoints();

        var tokenValue = "oc_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var categoryId = await SeedAsync(app, tokenValue, scopes);
        await app.StartAsync();

        return new TestApp(app, connection, tokenValue, categoryId);
    }

    /// <summary>Seeds the in-memory database with one user, one category, and one API token.</summary>
    private static async Task<int> SeedAsync(WebApplication app, string tokenValue, string[] scopes)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        var user = new User
        {
            Email = "api-token-test@example.com",
            DisplayName = "API Token Test",
            PasswordHash = "not-used",
        };
        var category = new Category
        {
            Name = "Food",
            Type = CategoryType.Expense,
            Icon = "Utensils",
            Color = "#DC2626",
            SortOrder = 1,
            SystemCode = "food",
        };
        db.Users.Add(user);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id,
            Name = "test token",
            TokenHash = BCrypt.Net.BCrypt.HashPassword(tokenValue),
            Prefix = tokenValue[..12],
            Scopes = scopes.Length == 0 ? null : JsonSerializer.Serialize(scopes),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return category.Id;
    }

    private sealed record TestApp(WebApplication App, SqliteConnection Connection, string TokenValue, int CategoryId) : IAsyncDisposable
    {
        /// <summary>Disposes the test host and in-memory SQLite connection.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
