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

        var allowedResponse = await client.PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = "修改名稱",
            market = "Twse",
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
            market = "Tpex",
            currentPrice = 120,
        }, JsonOptions());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await db.Entry(stock).ReloadAsync();
        Assert.Equal(StockMarket.Tpex, (await db.Stocks.SingleAsync(item => item.Id == stock.Id)).Market);
    }

    /// <summary>驗證已有明確市場的 Ledger 股票不可切換至另一個市場。</summary>
    [Fact]
    public async Task StockApi_RejectsKnownMarketChange()
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

        var response = await app.GetTestClient().PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = "上櫃標的",
            market = "Tpex",
            currentPrice = 120,
        }, JsonOptions());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LedgerManagedIdentityReadOnly", error.GetProperty("code").GetString());
    }

    /// <summary>驗證股票更新只接受 metadata contract，且不會覆寫 Ledger projection 與 identity。</summary>
    [Fact]
    public async Task StockApi_AcceptsRestrictedMetadataUpdateContract()
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

        var response = await app.GetTestClient().PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = " 更新名稱 ",
            market = "Twse",
            currentPrice = 120,
            lastPriceUpdate = "2026-08-25T00:00:00Z",
        }, JsonOptions());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await db.Stocks.AsNoTracking().SingleAsync(item => item.Id == stock.Id);
        Assert.Equal("更新名稱", updated.Name);
        Assert.Equal("2330", updated.Symbol);
        Assert.Equal(StockInstrumentType.Stock, updated.InstrumentType);
        Assert.Null(updated.Broker);
        Assert.Equal(10m, updated.Shares);
        Assert.Equal(100m, updated.BuyPrice);
        Assert.Equal(120m, updated.CurrentPrice);
    }

    /// <summary>驗證 Ledger 股票遭竄改受保護欄位時回傳 typed conflict。</summary>
    [Fact]
    public async Task StockApi_RejectsTamperedProtectedFieldsWithTypedConflict()
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

        var response = await app.GetTestClient().PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = "更新名稱",
            market = "Twse",
            currentPrice = 120,
            lastPriceUpdate = "2026-08-25T00:00:00Z",
            symbol = "6488",
            broker = "竄改券商",
            instrumentType = "BondEtf",
            shares = 999,
            buyPrice = 1,
        }, JsonOptions());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LedgerManagedFieldsReadOnly", error.GetProperty("code").GetString());
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

    /// <summary>驗證 options 不受持股分頁限制，且同代號券商與排序資訊完整保留。</summary>
    [Fact]
    public async Task StockOptions_ReturnsAllStocksWithBrokerAwareDeterministicOrdering()
    {
        await using var db = await CreateDbContextAsync();
        for (var index = 0; index < 40; index++)
        {
            db.Stocks.Add(new Stock
            {
                Name = $"標的 {index:00}",
                Symbol = $"{index + 1000:0000}",
                Market = StockMarket.Twse,
                Shares = 10m,
                BuyPrice = 100m,
                CurrentPrice = 110m,
                Broker = "一般券商",
            });
        }
        db.Stocks.AddRange(
            new Stock { Name = "台積電", Symbol = "2330", Market = StockMarket.Twse, Shares = 10m, BuyPrice = 500m, CurrentPrice = 600m, Broker = "元大證券" },
            new Stock { Name = "台積電", Symbol = "2330", Market = StockMarket.Twse, Shares = 10m, BuyPrice = 500m, CurrentPrice = 600m, Broker = "富邦證券" });
        await db.SaveChangesAsync();
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().GetAsync("/api/stocks/options");
        response.EnsureSuccessStatusCode();
        var options = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions());

        Assert.NotNull(options);
        Assert.Equal(42, options!.Length);
        var brokerOptions = options.Where(item => item.GetProperty("symbol").GetString() == "2330").ToArray();
        Assert.Equal(["元大證券", "富邦證券"], brokerOptions.Select(item => item.GetProperty("broker").GetString() ?? string.Empty).ToArray());
        Assert.All(options, item => Assert.True(item.TryGetProperty("hasLedger", out _)));
    }

    /// <summary>驗證 options 預設隱藏已結清持股，includeClosed=true 可回傳其 Ledger identity。</summary>
    [Fact]
    public async Task StockOptions_IncludesClosedHoldingsWhenRequested()
    {
        await using var db = await CreateDbContextAsync();
        var closed = await AddStockAsync(db, "2330", StockMarket.Twse);
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
        await using var app = await CreateAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var active = await client.GetFromJsonAsync<JsonElement[]>("/api/stocks/options", JsonOptions());
        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/stocks/options?includeClosed=true", JsonOptions());

        Assert.Empty(active!);
        var closedOption = Assert.Single(all!);
        Assert.Equal(0m, closedOption.GetProperty("shares").GetDecimal());
        Assert.True(closedOption.GetProperty("hasLedger").GetBoolean());
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
