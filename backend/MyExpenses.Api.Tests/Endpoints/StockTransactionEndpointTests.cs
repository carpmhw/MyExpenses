using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public sealed class StockTransactionEndpointTests
{
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
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var app = builder.Build();
        app.MapStockEndpoints();
        app.MapStockTransactionEndpoints();
        await app.StartAsync();
        return app;
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
        string symbol = "2330")
    {
        var stock = new Stock
        {
            Name = "測試標的",
            Symbol = symbol,
            Market = StockMarket.Twse,
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
