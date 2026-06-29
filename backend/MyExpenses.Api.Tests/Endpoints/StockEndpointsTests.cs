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
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class StockEndpointsTests
{
    /// <summary>Verifies stock list totals are calculated before pagination is applied.</summary>
    [Fact]
    public async Task ListStocks_ReturnsAllHoldingValuationTotalsBeforePagination()
    {
        await using var db = await CreateDbContextAsync();

        var result = await StockEndpoints.ListStocksAsync(1, 1, db);

        Assert.Single(result.Items);
        Assert.Equal(2, result.Total);
        Assert.Equal(1096362m, result.TotalEstimatedNetSellValue);
        Assert.Equal(55943m, result.TotalEstimatedGainLoss);
    }

    /// <summary>Verifies stock list rows expose per-holding valuation fields.</summary>
    [Fact]
    public async Task ListStocks_ReturnsPerHoldingValuationFields()
    {
        await using var db = await CreateDbContextAsync();

        var result = await StockEndpoints.ListStocksAsync(1, 10, db);
        var item = Assert.Single(result.Items, s => s.Symbol == "2330");

        Assert.Equal(StockInstrumentType.Stock, item.InstrumentType);
        Assert.Equal(1050000m, item.GrossMarketValue);
        Assert.Equal(1046432m, item.EstimatedNetSellValue);
        Assert.Equal(46033m, item.EstimatedGainLoss);
    }

    /// <summary>Verifies stock list queries filter by trimmed symbol keyword.</summary>
    [Fact]
    public async Task ListStocks_FiltersByTrimmedSymbolKeyword()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var result = await app.GetTestClient().GetFromJsonAsync<StockListResponse>(
            "/api/stocks?page=1&pageSize=10&symbol=%20233%20", CreateJsonOptions());

        var item = Assert.Single(result!.Items);
        Assert.Equal("2330", item.Symbol);
        Assert.Equal(1, result.Total);
    }

    /// <summary>Verifies stock list queries filter by trimmed broker keyword.</summary>
    [Fact]
    public async Task ListStocks_FiltersByTrimmedBrokerKeyword()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var result = await app.GetTestClient().GetFromJsonAsync<StockListResponse>(
            "/api/stocks?page=1&pageSize=10&broker=%20%E5%85%83%E5%A4%A7%20", CreateJsonOptions());

        var item = Assert.Single(result!.Items);
        Assert.Equal("元大證券", item.Broker);
        Assert.Equal("2330", item.Symbol);
    }

    /// <summary>Verifies symbol and broker stock filters are combined with AND semantics.</summary>
    [Fact]
    public async Task ListStocks_FiltersBySymbolAndBrokerKeywords()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var result = await app.GetTestClient().GetFromJsonAsync<StockListResponse>(
            "/api/stocks?page=1&pageSize=10&symbol=005&broker=%E5%AF%8C%E9%82%A6", CreateJsonOptions());

        var item = Assert.Single(result!.Items);
        Assert.Equal("0050", item.Symbol);
        Assert.Equal("富邦證券", item.Broker);
    }

    /// <summary>Verifies blank stock filters are ignored.</summary>
    [Fact]
    public async Task ListStocks_IgnoresBlankFilters()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var result = await app.GetTestClient().GetFromJsonAsync<StockListResponse>(
            "/api/stocks?page=1&pageSize=10&symbol=%20%20%20&broker=%20", CreateJsonOptions());

        Assert.Equal(2, result!.Items.Count);
        Assert.Equal(2, result.Total);
    }

    /// <summary>Verifies filtered valuation totals are calculated before pagination.</summary>
    [Fact]
    public async Task ListStocks_ReturnsFilteredTotalsBeforePagination()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var result = await app.GetTestClient().GetFromJsonAsync<StockListResponse>(
            "/api/stocks?page=1&pageSize=1&broker=%E8%AD%89%E5%88%B8", CreateJsonOptions());

        Assert.Single(result!.Items);
        Assert.Equal(2, result.Total);
        Assert.Equal(1096362m, result.TotalEstimatedNetSellValue);
        Assert.Equal(55943m, result.TotalEstimatedGainLoss);
    }

    /// <summary>Verifies stock creation trims text fields before saving.</summary>
    [Fact]
    public async Task CreateStock_TrimsTextFieldsBeforeSaving()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().PostAsJsonAsync("/api/stocks", new Stock
        {
            Name = " 新股票 ",
            Symbol = " 9999 ",
            InstrumentType = StockInstrumentType.Stock,
            Shares = 100m,
            BuyPrice = 10m,
            CurrentPrice = 11m,
            Broker = " 凱基證券 ",
        }, CreateJsonOptions());

        response.EnsureSuccessStatusCode();
        var stock = await db.Stocks.SingleAsync(s => s.Symbol.Trim() == "9999");
        Assert.Equal("新股票", stock.Name);
        Assert.Equal("9999", stock.Symbol);
        Assert.Equal("凱基證券", stock.Broker);
    }

    /// <summary>Verifies stock updates trim text fields and preserve blank broker as an empty string.</summary>
    [Fact]
    public async Task UpdateStock_TrimsTextFieldsAndPreservesBlankBroker()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var stock = await db.Stocks.SingleAsync(s => s.Symbol == "2330");

        var response = await app.GetTestClient().PutAsJsonAsync($"/api/stocks/{stock.Id}", new Stock
        {
            Name = " 台積電更新 ",
            Symbol = " 2330 ",
            InstrumentType = StockInstrumentType.BondEtf,
            Shares = stock.Shares,
            BuyPrice = stock.BuyPrice,
            CurrentPrice = stock.CurrentPrice,
            Broker = "   ",
            LastPriceUpdate = stock.LastPriceUpdate,
        }, CreateJsonOptions());

        response.EnsureSuccessStatusCode();
        await db.Entry(stock).ReloadAsync();
        Assert.Equal("台積電更新", stock.Name);
        Assert.Equal("2330", stock.Symbol);
        Assert.Equal(string.Empty, stock.Broker);
        Assert.Equal(StockInstrumentType.BondEtf, stock.InstrumentType);
    }

    /// <summary>Creates a stock API test application backed by the supplied SQLite connection.</summary>
    private static async Task<WebApplication> CreateStockAppAsync(SqliteConnection connection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddHttpClient();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        app.MapStockEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>Creates JSON options that match API enum string serialization.</summary>
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Creates a SQLite-backed context with two stock holdings.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Stocks.AddRange(
            new Stock { Name = "台積電", Symbol = "2330", InstrumentType = StockInstrumentType.Stock, Shares = 1000m, BuyPrice = 1000m, CurrentPrice = 1050m, Broker = "元大證券" },
            new Stock { Name = "台灣50", Symbol = "0050", InstrumentType = StockInstrumentType.StockEtf, Shares = 1000m, BuyPrice = 40m, CurrentPrice = 50m, Broker = "富邦證券" });
        await db.SaveChangesAsync();

        return db;
    }
}
