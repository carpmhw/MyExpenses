using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public sealed class TransactionDefaultsApiTests
{
    /// <summary>驗證查詢空預設與分別指定、替換、重送不會互相覆蓋或改變時區。</summary>
    [Fact]
    public async Task Defaults_ReadReplaceAndReplayIndependently()
    {
        await using var test = await CreateTestAsync();
        var client = OwnerClient(test.App);
        Assert.Equal(new Defaults(null, null), await ReadAsync(client));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/bank-accounts/1/default", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/credit-cards/1/default", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/bank-accounts/2/default", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/credit-cards/1/default", null)).StatusCode);
        Assert.Equal(new Defaults(2, 1), await ReadAsync(client));
        await using var db = CreateDb(test.Connection);
        Assert.Equal("America/New_York", (await db.SystemSettings.SingleAsync(s => s.Id == 1)).TimeZoneId);
        Assert.Empty(await db.Withdrawals.ToListAsync());
        Assert.Empty(await db.Installments.ToListAsync());
    }

    /// <summary>驗證不存在或無效的目標不會清除已保存的預設。</summary>
    [Theory]
    [InlineData("bank-accounts", 0, HttpStatusCode.BadRequest)]
    [InlineData("credit-cards", -1, HttpStatusCode.BadRequest)]
    [InlineData("bank-accounts", 999, HttpStatusCode.NotFound)]
    [InlineData("credit-cards", 999, HttpStatusCode.NotFound)]
    public async Task Defaults_RejectInvalidTargetWithoutChangingState(string resource, int id, HttpStatusCode expected)
    {
        await using var test = await CreateTestAsync();
        var client = OwnerClient(test.App);
        await client.PutAsync("/api/bank-accounts/1/default", null);
        await client.PutAsync("/api/credit-cards/1/default", null);
        Assert.Equal(expected, (await client.PutAsync($"/api/{resource}/{id}/default", null)).StatusCode);
        Assert.Equal(new Defaults(1, 1), await ReadAsync(client));
    }

    /// <summary>驗證新的登入 session 與重建的 host 從 SQLite 讀取已保存設定。</summary>
    [Fact]
    public async Task Defaults_PersistAcrossSessionsAndHostRestart()
    {
        await using var test = await CreateTestAsync();
        var first = OwnerClient(test.App);
        Assert.Equal(HttpStatusCode.OK, (await first.PutAsync("/api/bank-accounts/2/default", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.PutAsync("/api/credit-cards/2/default", null)).StatusCode);
        Assert.Equal(new Defaults(2, 2), await ReadAsync(OwnerClient(test.App)));
        await test.App.DisposeAsync();
        test.App = await CreateAppAsync(test.Connection);
        Assert.Equal(new Defaults(2, 2), await ReadAsync(OwnerClient(test.App)));
    }

    /// <summary>驗證不同 context 交錯寫入時，即使追蹤值過期仍確實保存明確目標且不覆蓋其他欄位。</summary>
    [Fact]
    public async Task Defaults_ExplicitTargetOverridesStaleTrackingWithoutClobberingOtherFields()
    {
        await using var test = await CreateTestAsync();
        var client = OwnerClient(test.App);
        await client.PutAsync("/api/bank-accounts/1/default", null);
        await client.PutAsync("/api/credit-cards/1/default", null);
        await using var bankContext = CreateDb(test.Connection);
        await using var cardContext = CreateDb(test.Connection);
        await bankContext.SystemSettings.SingleAsync(s => s.Id == 1);
        await cardContext.SystemSettings.SingleAsync(s => s.Id == 1);
        await client.PutAsync("/api/bank-accounts/2/default", null);
        await client.PutAsync("/api/credit-cards/2/default", null);
        await using (var other = CreateDb(test.Connection))
            await other.SystemSettings.Where(s => s.Id == 1).ExecuteUpdateAsync(setters => setters.SetProperty(s => s.TimeZoneId, "Asia/Tokyo"));

        var bankResult = await new TransactionDefaultsService(bankContext).SetBankAccountAsync(1);
        Assert.Equal(1, bankResult!.DefaultBankAccountId);
        Assert.Equal(2, bankResult.DefaultCreditCardId);
        var cardResult = await new TransactionDefaultsService(cardContext).SetCreditCardAsync(1);
        Assert.Equal(1, cardResult!.DefaultBankAccountId);
        Assert.Equal(1, cardResult.DefaultCreditCardId);
        await using var after = CreateDb(test.Connection);
        Assert.Equal("Asia/Tokyo", (await after.SystemSettings.SingleAsync(s => s.Id == 1)).TimeZoneId);
    }

    /// <summary>驗證預設設定端點對匿名及具完整現有 scopes 的 API token 保持封閉。</summary>
    [Theory]
    [InlineData("GET", "/api/settings/transaction-defaults")]
    [InlineData("PUT", "/api/bank-accounts/1/default")]
    [InlineData("PUT", "/api/credit-cards/1/default")]
    public async Task Defaults_RequireOwnerAndRejectApiTokens(string method, string path)
    {
        await using var test = await CreateTestAsync();
        var client = test.App.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", test.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path))).StatusCode);
        Assert.Equal(new Defaults(null, null), await ReadAsync(OwnerClient(test.App)));
    }

    /// <summary>驗證成功刪除的原子 SetNull 不影響另一類別，刪除非預設不改變設定。</summary>
    [Theory]
    [InlineData("bank-accounts")]
    [InlineData("credit-cards")]
    public async Task Defaults_ClearOnlyAfterSuccessfulDeletion(string resource)
    {
        await using var test = await CreateTestAsync();
        var client = OwnerClient(test.App);
        await client.PutAsync("/api/bank-accounts/1/default", null);
        await client.PutAsync("/api/credit-cards/1/default", null);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/{resource}/2")).StatusCode);
        Assert.Equal(new Defaults(1, 1), await ReadAsync(client));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/{resource}/1")).StatusCode);
        Assert.Equal(resource == "bank-accounts" ? new Defaults(null, 1) : new Defaults(1, null), await ReadAsync(client));
    }

    /// <summary>驗證提款關聯阻止刪除帳戶時，同一交易的預設清空亦回滾。</summary>
    [Fact]
    public async Task Defaults_RetainReferenceWhenRestrictedDeletionFails()
    {
        await using var test = await CreateTestAsync();
        var client = OwnerClient(test.App);
        await client.PutAsync("/api/bank-accounts/1/default", null);
        await using (var db = CreateDb(test.Connection))
        {
            db.Withdrawals.Add(new Withdrawal { BankAccountId = 1, Amount = 10m, Date = new DateOnly(2026, 10, 1) });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.DeleteAsync("/api/bank-accounts/1")).StatusCode);
        Assert.Equal(new Defaults(1, null), await ReadAsync(client));
        await using var after = CreateDb(test.Connection);
        Assert.True(await after.BankAccounts.AnyAsync(a => a.Id == 1));
        Assert.Single(await after.Withdrawals.ToListAsync());
    }

    /// <summary>讀取公開設定 response，避免以服務內部狀態作為期望值。</summary>
    private static async Task<Defaults> ReadAsync(HttpClient client)
        => (await client.GetFromJsonAsync<Defaults>("/api/settings/transaction-defaults"))!;

    /// <summary>建立獨立的 owner 登入 session。</summary>
    private static HttpClient OwnerClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Owner", "1");
        return client;
    }

    /// <summary>建立與既有測試共享連線但不共享追蹤狀態的 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

    /// <summary>建立具真實 token middleware、owner 認證與設定路由的測試 host。</summary>
    private static async Task<WebApplication> CreateAppAsync(SqliteConnection connection)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
        builder.Services.AddSingleton<TimeZoneService>();
        builder.Services.AddScoped<TransactionDefaultsService>();
        builder.Services.AddAuthentication("Owner").AddScheme<AuthenticationSchemeOptions, OwnerHandler>("Owner", _ => { });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<ApiTokenAuthMiddleware>();
        app.UseMiddleware<ApiTokenScopeMiddleware>();
        app.UseAuthorization();
        app.MapSettingsEndpoints();
        app.MapBankAccountEndpoints();
        app.MapCreditCardEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>在隔離 SQLite 種入兩類各兩筆資源、非預設時區與真實 API token。</summary>
    private static async Task<TestApp> CreateTestAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var db = CreateDb(connection))
        {
            await db.Database.EnsureCreatedAsync();
            db.BankAccounts.AddRange(new BankAccount { Id = 1, BankName = "A", AccountNumber = "12345" }, new BankAccount { Id = 2, BankName = "B", AccountNumber = "23456" });
            db.CreditCards.AddRange(new CreditCard { Id = 1, BankName = "A", LastFourDigits = "1234" }, new CreditCard { Id = 2, BankName = "B", LastFourDigits = "2345" });
            db.SystemSettings.Add(new SystemSetting { TimeZoneId = "America/New_York" });
            db.Users.Add(new User { Id = 1, Email = "owner@example.com", DisplayName = "owner", PasswordHash = "unused" });
            await db.SaveChangesAsync();
            var token = "oc_" + Guid.NewGuid().ToString("N");
            var scopes = typeof(ApiTokenScopes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToArray();
            db.ApiTokens.Add(new ApiToken { UserId = 1, Name = "test", TokenHash = BCrypt.Net.BCrypt.HashPassword(token), Prefix = token[..12], Scopes = JsonSerializer.Serialize(scopes) });
            await db.SaveChangesAsync();
            return new TestApp(await CreateAppAsync(connection), connection, token);
        }
    }

    private sealed record Defaults(int? DefaultBankAccountId, int? DefaultCreditCardId);

    private sealed class TestApp(WebApplication app, SqliteConnection connection, string token) : IAsyncDisposable
    {
        public WebApplication App { get; set; } = app;
        public SqliteConnection Connection { get; } = connection;
        public string Token { get; } = token;

        /// <summary>釋放 host 與隔離資料庫。</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class OwnerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>以每個 request 的標頭模擬 owner 登入，不繞過 token middleware。</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.ContainsKey("X-Owner")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "Owner")), "Owner"))
                : AuthenticateResult.NoResult());
    }
}
