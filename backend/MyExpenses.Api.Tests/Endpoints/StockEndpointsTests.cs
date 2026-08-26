using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Net;
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

    /// <summary>驗證 legacy direct stock creation route 已停止提供。</summary>
    [Fact]
    public async Task LegacyStockCreateEndpoint_IsUnavailable()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().PostAsJsonAsync("/api/stocks", new
        {
            name = "不應建立",
            symbol = "9999",
            market = "Twse",
            instrumentType = "Stock",
            shares = 10,
            buyPrice = 100,
            currentPrice = 110,
        }, CreateJsonOptions());

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Empty(await db.Stocks.Where(stock => stock.Symbol == "9999").ToListAsync());
    }

    /// <summary>驗證更新持股時可由使用者修正交易市場。</summary>
    [Fact]
    public async Task UpdateStock_ChangesMarketWithoutChangingValuation()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var stock = await db.Stocks.SingleAsync(s => s.Symbol == "2330");
        var grossValue = stock.Shares * stock.CurrentPrice;

        var response = await app.GetTestClient().PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = stock.Name,
            market = "Twse",
            currentPrice = stock.CurrentPrice,
            lastPriceUpdate = stock.LastPriceUpdate,
        }, CreateJsonOptions());

        response.EnsureSuccessStatusCode();
        await db.Entry(stock).ReloadAsync();

        Assert.Equal(StockMarket.Twse, stock.Market);
        Assert.Equal(grossValue, stock.Shares * stock.CurrentPrice);
    }

    /// <summary>驗證股票更新只修改允許的 metadata 欄位。</summary>
    [Fact]
    public async Task UpdateStock_UpdatesAllowedMetadata()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var stock = await db.Stocks.SingleAsync(s => s.Symbol == "2330");

        var response = await app.GetTestClient().PutAsJsonAsync($"/api/stocks/{stock.Id}", new
        {
            name = " 台積電更新 ",
            market = "Twse",
            currentPrice = stock.CurrentPrice,
            lastPriceUpdate = stock.LastPriceUpdate,
        }, CreateJsonOptions());

        response.EnsureSuccessStatusCode();
        await db.Entry(stock).ReloadAsync();
        Assert.Equal("台積電更新", stock.Name);
        Assert.Equal("2330", stock.Symbol);
        Assert.Equal(StockInstrumentType.Stock, stock.InstrumentType);
    }

    /// <summary>Verifies lookup cache refresh does not update stored holding prices.</summary>
    [Fact]
    public async Task Lookup_DoesNotUpdateStoredStocksWhenRefreshingCache()
    {
        await using var db = await CreateDbContextAsync();
        var lookupSymbol = $"9{Guid.NewGuid():N}";
        db.Stocks.Add(new Stock
        {
            Name = "測試股票",
            Symbol = lookupSymbol,
            InstrumentType = StockInstrumentType.Stock,
            Shares = 100m,
            BuyPrice = 100m,
            CurrentPrice = 105m,
        });
        await db.SaveChangesAsync();

        await using var app = await CreateStockAppAsync(
            (SqliteConnection)db.Database.GetDbConnection(),
            catalogService: new FakeCatalogService(
                new OfficialMarketResolution(
                    StockMarket.Twse,
                    "Completed",
                    new CurrentPriceRecord(lookupSymbol, 1100m, "測試股票"))));

        var response = await app.GetTestClient().GetAsync($"/api/stocks/lookup?symbol={Uri.EscapeDataString(lookupSymbol)}");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        var stock = await db.Stocks.AsNoTracking().SingleAsync(s => s.Symbol == lookupSymbol);
        Assert.Equal("測試股票", payload.GetProperty("name").GetString());
        Assert.Equal(1100m, payload.GetProperty("currentPrice").GetDecimal());
        Assert.Equal(105m, stock.CurrentPrice);
        Assert.Null(stock.LastPriceUpdate);
    }

    /// <summary>驗證 lookup 會回傳唯一官方市場、名稱、價格及安全結果碼。</summary>
    [Fact]
    public async Task Lookup_ReturnsUniqueMarketNamePriceAndResultCode()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync(
            (SqliteConnection)db.Database.GetDbConnection(),
            catalogService: new FakeCatalogService(
                new OfficialMarketResolution(
                    StockMarket.Tpex,
                    "Completed",
                    new CurrentPriceRecord("6488", 88m, "環球晶"))));

        var response = await app.GetTestClient().GetAsync("/api/stocks/lookup?symbol=6488");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("環球晶", payload.GetProperty("name").GetString());
        Assert.Equal(88m, payload.GetProperty("currentPrice").GetDecimal());
        Assert.Equal("Tpex", payload.GetProperty("market").GetString());
        Assert.Equal("Completed", payload.GetProperty("resultCode").GetString());
    }

    /// <summary>驗證 lookup 遇到市場歧義時保持 Unknown 且不回傳代號市場。</summary>
    [Fact]
    public async Task Lookup_ReturnsUnknownForAmbiguousMarket()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync(
            (SqliteConnection)db.Database.GetDbConnection(),
            catalogService: new FakeCatalogService(
                new OfficialMarketResolution(StockMarket.Unknown, "AmbiguousMarket")));

        var response = await app.GetTestClient().GetAsync("/api/stocks/lookup?symbol=9999");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("currentPrice").ValueKind);
        Assert.Equal("Unknown", payload.GetProperty("market").GetString());
        Assert.Equal("AmbiguousMarket", payload.GetProperty("resultCode").GetString());
    }

    /// <summary>驗證 lookup 遇到官方來源失敗時回傳安全 Unknown 結果。</summary>
    [Fact]
    public async Task Lookup_ReturnsUnknownWhenCatalogUnavailable()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateStockAppAsync(
            (SqliteConnection)db.Database.GetDbConnection(),
            catalogService: new FakeCatalogService(
                new OfficialMarketResolution(
                    StockMarket.Unknown,
                    "MarketDetectionUnavailable",
                    Retryable: true,
                    SafeMessage: "官方市場清單暫時無法使用")));

        var response = await app.GetTestClient().GetAsync("/api/stocks/lookup?symbol=2330");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Unknown", payload.GetProperty("market").GetString());
        Assert.Equal("MarketDetectionUnavailable", payload.GetProperty("resultCode").GetString());
    }

    /// <summary>Creates a stock API test application backed by the supplied SQLite connection.</summary>
    private static async Task<WebApplication> CreateStockAppAsync(
        SqliteConnection connection,
        HttpMessageHandler? handler = null,
        IOfficialMarketCatalogService? catalogService = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        var httpClientBuilder = builder.Services.AddHttpClient(string.Empty);
        httpClientBuilder.ConfigurePrimaryHttpMessageHandler(() => handler ?? new HttpClientHandler());
        builder.Services.AddSingleton(catalogService ?? new FakeCatalogService(
            new OfficialMarketResolution(StockMarket.Unknown, "MarketNotFound")));
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        app.MapStockEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>Returns deterministic TWSE data for lookup endpoint tests.</summary>
    private sealed class StubTwseHandler : HttpMessageHandler
    {
        private readonly string _symbol;

        /// <summary>Initializes a handler that returns the requested test symbol.</summary>
        public StubTwseHandler(string symbol)
        {
            _symbol = symbol;
        }

        /// <summary>Builds a successful response containing one stock closing price.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(
                $"[{{\"Code\":\"{_symbol}\",\"Name\":\"測試股票\",\"ClosingPrice\":\"1100\"}}]",
                Encoding.UTF8,
                "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>提供 endpoint 測試固定的官方市場辨識結果。</summary>
    private sealed class FakeCatalogService : IOfficialMarketCatalogService
    {
        /// <summary>初始化固定市場辨識結果。</summary>
        public FakeCatalogService(OfficialMarketResolution resolution)
        {
            Resolution = resolution;
        }

        /// <summary>取得測試固定的辨識結果。</summary>
        public OfficialMarketResolution Resolution { get; }

        /// <summary>回傳固定的市場辨識結果。</summary>
        public Task<OfficialMarketResolution> LookupAsync(string? symbol, CancellationToken cancellationToken = default)
            => Task.FromResult(Resolution);

        /// <summary>回傳固定的官方快照，供介面完成。</summary>
        public Task<OfficialMarketCatalogSnapshot> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OfficialMarketCatalogSnapshot(
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
                CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)])));
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
