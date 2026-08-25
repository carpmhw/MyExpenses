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

public sealed class StockLedgerStockEndpointTests
{
    /// <summary>驗證有 Ledger 的股票只能修改允許欄位且不能直接刪除。</summary>
    [Fact]
    public async Task StockApi_ProtectsLedgerManagedFieldsAndDelete()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, "2330", StockMarket.Twse);
        var service = new StockLedgerService(db);
        await service.CreateTransactionAsync(stock.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Buy,
            new DateOnly(2026, 8, 25),
            Shares: 10m,
            Price: 100m));
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var protectedResponse = await client.PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = "修改名稱",
            symbol = "9999",
            market = "Twse",
            instrumentType = "Stock",
            shares = 10,
            buyPrice = 100,
            currentPrice = 120,
        }, JsonOptions());
        Assert.Equal(HttpStatusCode.Conflict, protectedResponse.StatusCode);
        Assert.Equal("LedgerManagedFieldsReadOnly", (await protectedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var allowedResponse = await client.PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = "修改名稱",
            symbol = "2330",
            market = "Twse",
            instrumentType = "Stock",
            shares = 10,
            buyPrice = 100,
            currentPrice = 120,
            lastPriceUpdate = "2026-08-25T00:00:00Z",
        }, JsonOptions());
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/stocks/{stock.Id}");
        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
        Assert.Contains("StockHasLedgerHistory", await deleteResponse.Content.ReadAsStringAsync());
    }

    /// <summary>驗證 Unknown 市場只允許透過已知上市或上櫃市場補正。</summary>
    [Fact]
    public async Task StockApi_AllowsUnknownMarketResolutionOnly()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, "00679B", StockMarket.Unknown);
        var service = new StockLedgerService(db);
        await service.CreateTransactionAsync(stock.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Buy,
            new DateOnly(2026, 8, 25),
            Shares: 10m,
            Price: 100m));
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var response = await app.GetTestClient().PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = "上櫃標的",
            symbol = "00679B",
            market = "Tpex",
            instrumentType = "Stock",
            shares = 10,
            buyPrice = 100,
            currentPrice = 120,
        }, JsonOptions());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await db.Entry(stock).ReloadAsync();
        Assert.Equal(StockMarket.Tpex, (await db.Stocks.SingleAsync(item => item.Id == stock.Id)).Market);
    }

    /// <summary>驗證預設股票列表隱藏已結清標的，includeClosed=true 會保留歷史主檔。</summary>
    [Fact]
    public async Task StockList_HidesClosedHoldingsUnlessRequested()
    {
        await using var db = await CreateDbContextAsync();
        var closed = await AddStockAsync(db, "2330", StockMarket.Twse);
        var open = await AddStockAsync(db, "0050", StockMarket.Twse);
        var service = new StockLedgerService(db);
        await service.CreateTransactionAsync(closed.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Buy,
            new DateOnly(2026, 8, 1),
            Shares: 10m,
            Price: 100m));
        await service.CreateTransactionAsync(closed.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Sell,
            new DateOnly(2026, 8, 2),
            Shares: 10m,
            Price: 110m));
        await service.CreateTransactionAsync(open.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Buy,
            new DateOnly(2026, 8, 1),
            Shares: 5m,
            Price: 50m));
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var active = await client.GetFromJsonAsync<StockListResponse>(
            "/api/stocks?page=1&pageSize=20", JsonOptions());
        var all = await client.GetFromJsonAsync<StockListResponse>(
            "/api/stocks?page=1&pageSize=20&includeClosed=true", JsonOptions());

        Assert.DoesNotContain(active!.Items, item => item.Symbol == "2330");
        Assert.Contains(all!.Items, item => item.Symbol == "2330");
    }

    /// <summary>建立使用 SQLite connection 的股票 endpoint 測試應用程式。</summary>
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

    /// <summary>建立開啟中的 SQLite 記憶體資料庫。</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    /// <summary>建立並保存測試用股票主檔。</summary>
    private static async Task<Stock> AddStockAsync(AppDbContext db, string symbol, StockMarket market)
    {
        var stock = new Stock
        {
            Name = "測試標的",
            Symbol = symbol,
            Market = market,
            Shares = 0m,
            BuyPrice = 0m,
            CurrentPrice = 100m,
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
