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
using MyExpenses.Api.Options;
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

    /// <summary>驗證 receipt replay 仍先檢查目前 token 授權，撤權後不得回傳財務資料。</summary>
    [Fact]
    public async Task PostTransaction_ReplayAfterTokenRevocationIsRejected()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsWrite);
        var client = CreateAuthorizedClient(app);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var request = new
        {
            type = TransactionType.Expense,
            amount = 120m,
            date = new DateOnly(2026, 9, 5),
            description = "撤權 replay",
            categoryId = app.CategoryId,
            paymentMethodId = app.CashPaymentMethodId,
        };

        var first = await client.PostAsJsonAsync("/api/transactions", request);
        await AssertStatusCodeAsync(HttpStatusCode.Created, first);
        await app.RevokeTokenAsync();

        var replay = await client.PostAsJsonAsync("/api/transactions", request);

        await AssertStatusCodeAsync(HttpStatusCode.Unauthorized, replay);
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

    /// <summary>驗證沒有 reports:read 的 API token 不可讀取股票績效。</summary>
    [Fact]
    public async Task GetStockPerformance_RejectsApiTokenWithoutReportsReadScope()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsRead);
        var response = await CreateAuthorizedClient(app).GetAsync("/api/reports/stock-performance");

        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, response);
    }

    /// <summary>驗證既有 reports:read scope 可讀取股票績效 empty state。</summary>
    [Fact]
    public async Task GetStockPerformance_AllowsApiTokenWithReportsReadScope()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.ReportsRead);
        var response = await CreateAuthorizedClient(app).GetAsync("/api/reports/stock-performance");

        await AssertStatusCodeAsync(HttpStatusCode.OK, response);
    }

    /// <summary>驗證即使具備所有 MCP 權限與新增唯讀權限，API token 仍不能建立其他 token。</summary>
    [Fact]
    public async Task AuthApiTokens_RejectsApiTokenEvenWithMcpScopes()
    {
        await using var app = await CreateAppAsync(
            ApiTokenScopes.TransactionsRead,
            ApiTokenScopes.TransactionsWrite,
            ApiTokenScopes.TransactionsUndo,
            ApiTokenScopes.CategoriesRead,
            ApiTokenScopes.PaymentMethodsRead,
            ApiTokenScopes.ReportsRead,
            ApiTokenScopes.CreditCardsRead,
            ApiTokenScopes.AgentContextRead);
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

    /// <summary>驗證 agent context scope 可讀取後端系統日期與時區。</summary>
    [Fact]
    public async Task AgentContext_AllowsTokenWithAgentContextReadScope()
    {
        await using var app = await CreateAppAsync("agent-context:read");

        var response = await CreateAuthorizedClient(app).GetAsync("/api/agent/context");

        await AssertStatusCodeAsync(HttpStatusCode.OK, response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Asia/Taipei", body.RootElement.GetProperty("timeZoneId").GetString());
        Assert.True(DateOnly.TryParse(body.RootElement.GetProperty("currentDate").GetString(), out _));
    }

    /// <summary>驗證缺少 agent context scope 的 token 不能取得日期 context。</summary>
    [Fact]
    public async Task AgentContext_RejectsTokenWithoutAgentContextReadScope()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsRead);

        var response = await CreateAuthorizedClient(app).GetAsync("/api/agent/context");

        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, response);
    }

    /// <summary>驗證信用卡讀取 scope 可取得完整分頁資訊。</summary>
    [Fact]
    public async Task CreditCards_AllowsReadScopeAndPreservesPaginationMetadata()
    {
        await using var app = await CreateAppAsync("credit-cards:read");

        var response = await CreateAuthorizedClient(app).GetAsync("/api/credit-cards?page=2&pageSize=5");

        await AssertStatusCodeAsync(HttpStatusCode.OK, response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(21, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(5, body.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, body.RootElement.GetProperty("items").GetArrayLength());
    }

    /// <summary>驗證缺少信用卡讀取 scope 的 token 不能列出信用卡。</summary>
    [Fact]
    public async Task CreditCards_RejectsTokenWithoutCreditCardsReadScope()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsRead);

        var response = await CreateAuthorizedClient(app).GetAsync("/api/credit-cards");

        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, response);
    }

    /// <summary>驗證 consumption 讀取需要 transactions:read scope。</summary>
    [Fact]
    public async Task Consumption_RejectsTokenWithoutTransactionsReadScope()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsWrite);

        var response = await CreateAuthorizedClient(app).GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30");

        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, response);
    }

    /// <summary>驗證 transactions:read token 可讀取 consumption 結果。</summary>
    [Fact]
    public async Task Consumption_AllowsTokenWithTransactionsReadScope()
    {
        await using var app = await CreateAppAsync(ApiTokenScopes.TransactionsRead);

        var response = await CreateAuthorizedClient(app).GetAsync(
            "/api/consumption?startDate=2026-09-01&endDate=2026-09-30");

        await AssertStatusCodeAsync(HttpStatusCode.OK, response);
    }

    /// <summary>驗證新增唯讀權限及所有唯讀權限的聯集都不能管理卡片、時區設定或 API token。</summary>
    [Theory]
    [InlineData(ApiTokenScopes.CreditCardsRead)]
    [InlineData(ApiTokenScopes.AgentContextRead)]
    [InlineData("all-read")]
    public async Task ReadOnlyScopes_CannotManageCardsSettingsOrTokens(string scope)
    {
        var scopes = scope == "all-read"
            ? new[] { ApiTokenScopes.TransactionsRead, ApiTokenScopes.CategoriesRead,
                ApiTokenScopes.PaymentMethodsRead, ApiTokenScopes.ReportsRead,
                ApiTokenScopes.CreditCardsRead, ApiTokenScopes.AgentContextRead }
            : new[] { scope };
        await using var app = await CreateAppAsync(scopes);
        var client = CreateAuthorizedClient(app);
        var card = new { bankName = "不得修改", lastFourDigits = "9999", statementDay = 15, dueDay = 23 };
        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, await client.PostAsJsonAsync("/api/credit-cards", card));
        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, await client.PutAsJsonAsync("/api/credit-cards/1", card));
        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, await client.DeleteAsync("/api/credit-cards/1"));
        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, await client.PutAsJsonAsync("/api/settings/timezone", new { timeZoneId = "UTC" }));
        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, await client.PutAsJsonAsync("/api/settings/time-zone", new { timeZoneId = "UTC" }));
        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, await client.GetAsync("/api/auth/api-tokens"));
        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, await client.PostAsJsonAsync("/api/auth/api-tokens",
            new { name = "不得建立", scopes = new[] { ApiTokenScopes.TransactionsWrite } }));
        await AssertStatusCodeAsync(HttpStatusCode.Forbidden, await client.DeleteAsync("/api/auth/api-tokens/1"));

        await using var verification = app.App.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(21, await db.CreditCards.CountAsync());
        Assert.Equal("測試銀行1", (await db.CreditCards.SingleAsync(item => item.Id == 1)).BankName);
        var token = await db.ApiTokens.SingleAsync();
        Assert.False(token.IsRevoked);
        Assert.Equal(JsonSerializer.Serialize(scopes), token.Scopes);
        Assert.Equal(0, await db.SystemSettings.CountAsync());
        Assert.Equal("Asia/Taipei", app.App.Services.GetRequiredService<TimeZoneService>().TimeZoneId);
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

    /// <summary>建立包含指定權限 API token 及受保護管理端點的測試應用程式。</summary>
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
        builder.Services.Configure<TimeZoneOptions>(_ => { });
        builder.Services.AddSingleton<TimeZoneService>();
        builder.Services.AddScoped<TransactionCommandService>();
        builder.Services.AddScoped<ConsumptionQueryService>();
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
        app.MapReportEndpoints();
        app.MapCreditCardEndpoints();
        app.MapAgentContextEndpoints();
        app.MapConsumptionEndpoints();
        app.MapSettingsEndpoints();

        var tokenValue = "oc_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var (categoryId, cashPaymentMethodId) = await SeedAsync(app, tokenValue, scopes);
        await app.StartAsync();

        return new TestApp(app, connection, tokenValue, categoryId, cashPaymentMethodId);
    }

    /// <summary>Seeds the in-memory database with one user, one category, and one API token.</summary>
    private static async Task<(int CategoryId, int CashPaymentMethodId)> SeedAsync(WebApplication app, string tokenValue, string[] scopes)
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
        var creditCards = Enumerable.Range(1, 21)
            .Select(index => new CreditCard
            {
                BankName = $"測試銀行{index}",
                LastFourDigits = index.ToString("D4"),
                StatementDay = 15,
                DueDay = 23,
            })
            .ToArray();
        var cashPaymentMethod = new PaymentMethod
        {
            Name = "現金",
            SystemCode = "cash",
        };
        db.Users.Add(user);
        db.Categories.Add(category);
        db.CreditCards.AddRange(creditCards);
        db.PaymentMethods.Add(cashPaymentMethod);
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

        return (category.Id, cashPaymentMethod.Id);
    }

    private sealed record TestApp(
        WebApplication App,
        SqliteConnection Connection,
        string TokenValue,
        int CategoryId,
        int CashPaymentMethodId) : IAsyncDisposable
    {
        /// <summary>撤銷測試 API token 以驗證 receipt replay 的目前授權檢查。</summary>
        public async Task RevokeTokenAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var token = await db.ApiTokens.SingleAsync(item => item.TokenHash != null);
            token.IsRevoked = true;
            await db.SaveChangesAsync();
        }

        /// <summary>Disposes the test host and in-memory SQLite connection.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
