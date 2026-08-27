using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public sealed class StockTransactionEndpointTests
{
    /// <summary>驗證費稅估算 endpoint 回傳 Buy 與 Sell 的 gross、佣金及交易稅。</summary>
    [Fact]
    public async Task EstimateCostsApi_ReturnsBuyAndSellEstimatesWithoutMutation()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, shares: 100m, buyPrice: 100m, currentPrice: 105m);
        var originalShares = stock.Shares;
        var originalBuyPrice = stock.BuyPrice;
        var originalTransactionCount = await db.StockTransactions.CountAsync();
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var buyResponse = await client.PostAsJsonAsync(
            "/api/stocks/ledger/estimate-costs",
            new { stockId = stock.Id, type = "Buy", shares = 100, price = 105 },
            JsonOptions());
        var sellResponse = await client.PostAsJsonAsync(
            "/api/stocks/ledger/estimate-costs",
            new { stockId = stock.Id, type = "Sell", shares = 100, price = 105 },
            JsonOptions());

        Assert.Equal(HttpStatusCode.OK, buyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sellResponse.StatusCode);
        var buy = await buyResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        var sell = await sellResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        Assert.Equal(10500m, buy.GetProperty("grossAmount").GetDecimal());
        Assert.Equal(20m, buy.GetProperty("fee").GetDecimal());
        Assert.Equal(0m, buy.GetProperty("tax").GetDecimal());
        Assert.Equal(31m, sell.GetProperty("tax").GetDecimal());
        Assert.Equal(originalShares, (await db.Stocks.SingleAsync(item => item.Id == stock.Id)).Shares);
        Assert.Equal(originalBuyPrice, (await db.Stocks.SingleAsync(item => item.Id == stock.Id)).BuyPrice);
        Assert.Equal(originalTransactionCount, await db.StockTransactions.CountAsync());
    }

    /// <summary>驗證不存在股票回傳 stable NotFound error contract。</summary>
    [Fact]
    public async Task EstimateCostsApi_ReturnsNotFoundForMissingStock()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/api/stocks/ledger/estimate-costs",
            new { stockId = 999, type = "Buy", shares = 1, price = 100 },
            JsonOptions());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        Assert.Equal("NotFound", error.GetProperty("code").GetString());
    }

    /// <summary>驗證無效股數回傳 400 invalid error 且不產生零費稅成功結果。</summary>
    [Fact]
    public async Task EstimateCostsApi_ReturnsInvalidInputForNonPositiveShares()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, shares: 100m, buyPrice: 100m, currentPrice: 105m);
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/api/stocks/ledger/estimate-costs",
            new { stockId = stock.Id, type = "Buy", shares = 0, price = 100 },
            JsonOptions());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        Assert.Equal("InvalidTransactionCostEstimateInput", error.GetProperty("code").GetString());
        Assert.Equal("NonPositiveShares", error.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(0, await db.StockTransactions.CountAsync());
    }

    /// <summary>驗證無法解析或超出 decimal 範圍的 body 仍回傳 typed invalid error。</summary>
    [Fact]
    public async Task EstimateCostsApi_ReturnsTypedInvalidErrorForMalformedDecimal()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, shares: 100m, buyPrice: 100m, currentPrice: 105m);
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());

        using var content = new StringContent(
            $"{{\"stockId\":{stock.Id},\"type\":\"Buy\",\"shares\":1e999,\"price\":100}}",
            Encoding.UTF8,
            "application/json");
        var response = await app.GetTestClient().PostAsync(
            "/api/stocks/ledger/estimate-costs",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        Assert.Equal("InvalidTransactionCostEstimateInput", error.GetProperty("code").GetString());
        Assert.Equal("InvalidRequestBody", error.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(0, await db.StockTransactions.CountAsync());
    }

    /// <summary>驗證空 body 與缺少必要欄位都回傳估算 endpoint 的 typed invalid error。</summary>
    [Theory]
    [InlineData("", "InvalidRequestBody")]
    [InlineData("{\"stockId\":1,\"type\":\"Buy\",\"price\":100}", "MissingShares")]
    [InlineData("{\"stockId\":1,\"shares\":1,\"price\":100}", "MissingTransactionType")]
    public async Task EstimateCostsApi_ReturnsTypedInvalidErrorForMissingRequestData(
        string body,
        string reason)
    {
        await using var db = await CreateDbContextAsync();
        await AddStockAsync(db, shares: 100m, buyPrice: 100m, currentPrice: 105m);
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await app.GetTestClient().PostAsync(
            "/api/stocks/ledger/estimate-costs",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        Assert.Equal("InvalidTransactionCostEstimateInput", error.GetProperty("code").GetString());
        Assert.Equal(reason, error.GetProperty("details").GetProperty("reason").GetString());
    }

    /// <summary>驗證未知市場與不支援交易類型回傳 422 及穩定 unsupported reason。</summary>
    [Fact]
    public async Task EstimateCostsApi_ReturnsUnsupportedReasons()
    {
        await using var db = await CreateDbContextAsync();
        var unknownMarket = await AddStockAsync(
            db,
            shares: 100m,
            buyPrice: 100m,
            currentPrice: 105m,
            symbol: "UNKNOWN",
            market: StockMarket.Unknown);
        var supportedMarket = await AddStockAsync(
            db,
            shares: 100m,
            buyPrice: 100m,
            currentPrice: 105m,
            symbol: "DIVIDEND");
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var marketResponse = await client.PostAsJsonAsync(
            "/api/stocks/ledger/estimate-costs",
            new { stockId = unknownMarket.Id, type = "Buy", shares = 1, price = 100 },
            JsonOptions());
        var typeResponse = await client.PostAsJsonAsync(
            "/api/stocks/ledger/estimate-costs",
            new { stockId = supportedMarket.Id, type = "Dividend", shares = 1, price = 100 },
            JsonOptions());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, marketResponse.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, typeResponse.StatusCode);
        var marketError = await marketResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        var typeError = await typeResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        Assert.Equal("TransactionCostEstimationUnsupported", marketError.GetProperty("code").GetString());
        Assert.Equal("UnsupportedMarket", marketError.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal("UnsupportedTransactionType", typeError.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(0, await db.StockTransactions.CountAsync());
    }

    /// <summary>驗證費稅估算 endpoint 沿用 global fallback policy 拒絕匿名呼叫。</summary>
    [Fact]
    public async Task EstimateCostsApi_RejectsAnonymousCaller()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, shares: 100m, buyPrice: 100m, currentPrice: 105m);
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        var response = await client.PostAsJsonAsync(
            "/api/stocks/ledger/estimate-costs",
            new { stockId = stock.Id, type = "Buy", shares = 1, price = 100 },
            JsonOptions());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>驗證交易 endpoint 可建立、列出、取得、修改與刪除並回傳 replay 衍生欄位。</summary>
    [Fact]
    public async Task LedgerApi_SupportsCrudAndDerivedFields()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, shares: 0m, buyPrice: 0m, currentPrice: 100m);
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var createResponse = await client.PostAsJsonAsync(
            "/api/stocks/ledger/transactions",
            new
            {
                stockId = stock.Id,
                type = "Buy",
                tradeDate = "2026-08-25",
                shares = 10,
                price = 100,
                fee = 2,
                tax = 1,
                notes = " first buy ",
            },
            JsonOptions());
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        var transactionId = created.GetProperty("id").GetInt32();

        var list = await client.GetFromJsonAsync<StockTransactionListResponse>(
            $"/api/stocks/ledger?stockId={stock.Id}&type=Buy&page=1&pageSize=10",
            JsonOptions());
        var item = Assert.Single(list!.Items);
        Assert.Equal(transactionId, item.Id);
        Assert.Equal(10m, item.RemainingShares);
        Assert.Equal(1003m, item.RemainingCostBasis);
        Assert.Equal("first buy", item.Notes);

        var getResponse = await client.GetAsync($"/api/stocks/ledger/{transactionId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/stocks/ledger/{transactionId}",
            new
            {
                stockId = stock.Id,
                type = "Buy",
                tradeDate = "2026-08-25",
                shares = 5,
                price = 120,
                fee = 0,
                tax = 0,
            },
            JsonOptions());
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/stocks/ledger/{transactionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Empty(await db.StockTransactions.ToListAsync());
    }

    /// <summary>驗證 API 可建立股票股利並回傳零現金流與完整 replay 欄位，也可用 type filter 查詢。</summary>
    [Fact]
    public async Task LedgerApi_CreatesAndFiltersStockDividend()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, shares: 0m, buyPrice: 0m, currentPrice: 100m);
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var buyResponse = await client.PostAsJsonAsync(
            "/api/stocks/ledger/transactions",
            new
            {
                stockId = stock.Id,
                type = "Buy",
                tradeDate = "2026-01-01",
                shares = 1000m,
                price = 100m,
                fee = 0m,
                tax = 0m,
            },
            JsonOptions());
        Assert.Equal(HttpStatusCode.Created, buyResponse.StatusCode);

        var response = await client.PostAsJsonAsync(
            "/api/stocks/ledger/transactions",
            new
            {
                stockId = stock.Id,
                type = "StockDividend",
                tradeDate = "2026-02-01",
                shares = 100m,
                price = (decimal?)null,
                fee = 0m,
                tax = 0m,
                cashAmount = (decimal?)null,
                openingMarketValue = (decimal?)null,
            },
            JsonOptions());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        Assert.Equal("StockDividend", payload.GetProperty("type").GetString());
        Assert.Equal(100m, payload.GetProperty("shares").GetDecimal());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("price").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("cashAmount").ValueKind);
        Assert.Equal(0m, payload.GetProperty("fee").GetDecimal());
        Assert.Equal(0m, payload.GetProperty("tax").GetDecimal());
        Assert.Equal(0m, payload.GetProperty("grossAmount").GetDecimal());
        Assert.Equal(0m, payload.GetProperty("netCashFlow").GetDecimal());
        Assert.Equal(1100m, payload.GetProperty("remainingShares").GetDecimal());
        Assert.Equal(100000m, payload.GetProperty("remainingCostBasis").GetDecimal());

        var list = await client.GetFromJsonAsync<StockTransactionListResponse>(
            $"/api/stocks/ledger?stockId={stock.Id}&type=StockDividend",
            JsonOptions());
        var item = Assert.Single(list!.Items);
        Assert.Equal("StockDividend", item.Type.ToString());
        Assert.Equal(100m, item.Shares);
    }

    /// <summary>驗證股票股利帶入價格、現金、期初市值或非零費稅時回傳 typed InvalidTransaction。</summary>
    [Fact]
    public async Task LedgerApi_RejectsInvalidStockDividendFields()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, shares: 0m, buyPrice: 0m, currentPrice: 100m);
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();
        var requests = new[]
        {
            new { price = (decimal?)100m, cashAmount = (decimal?)null, openingMarketValue = (decimal?)null, fee = 0m, tax = 0m },
            new { price = (decimal?)null, cashAmount = (decimal?)100m, openingMarketValue = (decimal?)null, fee = 0m, tax = 0m },
            new { price = (decimal?)null, cashAmount = (decimal?)null, openingMarketValue = (decimal?)100m, fee = 0m, tax = 0m },
            new { price = (decimal?)null, cashAmount = (decimal?)null, openingMarketValue = (decimal?)null, fee = 1m, tax = 0m },
            new { price = (decimal?)null, cashAmount = (decimal?)null, openingMarketValue = (decimal?)null, fee = 0m, tax = 1m },
        };

        foreach (var request in requests)
        {
            var response = await client.PostAsJsonAsync(
                "/api/stocks/ledger/transactions",
                new
                {
                    stockId = stock.Id,
                    type = "StockDividend",
                    tradeDate = "2026-02-01",
                    shares = 1m,
                    request.price,
                    request.fee,
                    request.tax,
                    request.cashAmount,
                    request.openingMarketValue,
                },
                JsonOptions());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
            Assert.Equal("InvalidTransaction", error.GetProperty("code").GetString());
        }

        Assert.Empty(await db.StockTransactions.ToListAsync());
    }

    /// <summary>驗證一般交易建立不允許任意 OpeningBalance 且 oversell 回傳安全 typed error。</summary>
    [Fact]
    public async Task LedgerApi_RejectsOpeningCreateAndOversellSafely()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, shares: 5m, buyPrice: 100m, currentPrice: 100m);
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var openingResponse = await client.PostAsJsonAsync(
            "/api/stocks/ledger/transactions",
            new
            {
                stockId = stock.Id,
                type = "OpeningBalance",
                tradeDate = "2026-08-25",
                shares = 5,
                price = 100,
                openingMarketValue = 500,
            },
            JsonOptions());
        Assert.Equal(HttpStatusCode.BadRequest, openingResponse.StatusCode);
        Assert.Contains("OpeningBalanceNotAllowed", await openingResponse.Content.ReadAsStringAsync());

        var oversellResponse = await client.PostAsJsonAsync(
            "/api/stocks/ledger/transactions",
            new
            {
                stockId = stock.Id,
                type = "Sell",
                tradeDate = "2026-08-25",
                shares = 6,
                price = 100,
            },
            JsonOptions());
        Assert.Equal(HttpStatusCode.Conflict, oversellResponse.StatusCode);
        var error = await oversellResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InsufficientShares", error.GetProperty("code").GetString());
        Assert.DoesNotContain("StackTrace", await oversellResponse.Content.ReadAsStringAsync());
    }

    /// <summary>驗證 atomic position endpoint 同時建立股票、首筆交易與 Replay projection。</summary>
    [Fact]
    public async Task AtomicPositionApi_CreatesStockAndReplayProjection()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/api/stocks/positions",
            new
            {
                name = "測試標的",
                symbol = "2330",
                market = "Twse",
                instrumentType = "Stock",
                shares = 10,
                buyPrice = 100,
                currentPrice = 110,
                tradeDate = "2026-08-25",
                initialTransactionType = "Buy",
                broker = "元大證券",
            },
            JsonOptions());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions());
        Assert.Equal(10m, payload.GetProperty("stock").GetProperty("shares").GetDecimal());
        Assert.Equal(100m, payload.GetProperty("stock").GetProperty("buyPrice").GetDecimal());
        Assert.Equal(1, await db.Stocks.CountAsync());
        Assert.Equal(1, await db.StockTransactions.CountAsync());
    }

    /// <summary>驗證 atomic position endpoint 拒絕未定義市場並回滾建立流程。</summary>
    [Fact]
    public async Task AtomicPositionApi_RejectsUndefinedMarketWithoutCreatingStock()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/api/stocks/positions",
            new
            {
                name = "非法市場",
                symbol = "9999",
                market = 999,
                instrumentType = "Stock",
                shares = 10,
                buyPrice = 100,
                currentPrice = 110,
                tradeDate = "2026-08-25",
                initialTransactionType = "Buy",
            },
            JsonOptions());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await db.Stocks.ToListAsync());
        Assert.Empty(await db.StockTransactions.ToListAsync());
    }

    /// <summary>驗證初始化 endpoint 回傳 blocking 與 initialized counts 並保持冪等。</summary>
    [Fact]
    public async Task LedgerInitializationApi_ReturnsCountsAndIsIdempotent()
    {
        await using var db = await CreateDbContextAsync();
        var valid = await AddStockAsync(db, shares: 10m, buyPrice: 100m, currentPrice: 120m, symbol: "GOOD");
        await AddStockAsync(db, shares: 10m, buyPrice: 0m, currentPrice: 120m, symbol: "BLOCKED");
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var blocked = await client.PostAsJsonAsync(
            "/api/stocks/ledger/initialize",
            new { baselineDate = "2026-08-25" },
            JsonOptions());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Empty(await db.StockTransactions.ToListAsync());

        await db.Stocks.Where(stock => stock.Symbol == "BLOCKED").ExecuteUpdateAsync(setters =>
            setters.SetProperty(stock => stock.BuyPrice, 100m));
        var initialized = await client.PostAsJsonAsync(
            "/api/stocks/ledger/initialize",
            new { baselineDate = "2026-08-25" },
            JsonOptions());
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        var payload = await initialized.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, payload.GetProperty("initializedCount").GetInt32());
        Assert.Equal(2, await db.StockTransactions.CountAsync());
        Assert.Equal(10m, (await db.Stocks.SingleAsync(stock => stock.Id == valid.Id)).Shares);
    }

    /// <summary>建立使用 SQLite connection 的 endpoint 測試應用程式。</summary>
    private static async Task<WebApplication> CreateAppAsync(SqliteConnection connection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddScoped<StockLedgerService>();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapStockEndpoints();
        app.MapStockTransactionEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>提供可由 request header 切換匿名狀態的測試 authentication handler。</summary>
    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>取得測試 authentication scheme name。</summary>
        public const string SchemeName = "StockTest";

        /// <summary>依測試 request header 建立 authenticated principal 或匿名結果。</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.TryGetValue("X-Test-Anonymous", out var anonymous)
                && string.Equals(anonymous.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "stock-test-user")],
                Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    /// <summary>建立開啟中的 SQLite 記憶體連線與完整 schema。</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    /// <summary>建立並保存 endpoint 測試使用的股票主檔。</summary>
    private static async Task<Stock> AddStockAsync(
        AppDbContext db,
        decimal shares,
        decimal buyPrice,
        decimal currentPrice,
        string symbol = "2330",
        StockMarket market = StockMarket.Twse,
        StockInstrumentType instrumentType = StockInstrumentType.Stock)
    {
        var stock = new Stock
        {
            Name = "測試標的",
            Symbol = symbol,
            Market = market,
            InstrumentType = instrumentType,
            Shares = shares,
            BuyPrice = buyPrice,
            CurrentPrice = currentPrice,
        };
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        return stock;
    }

    /// <summary>建立與 API 相同的 JSON enum serializer options。</summary>
    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
