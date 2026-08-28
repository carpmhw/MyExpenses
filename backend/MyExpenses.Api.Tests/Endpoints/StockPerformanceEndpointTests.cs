using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public sealed class StockPerformanceEndpointTests
{
    /// <summary>驗證績效 endpoint 回傳完整報表 contract 並使用本機 raw close terminal value。</summary>
    [Fact]
    public async Task GetStockPerformance_ReturnsFullContractWithoutExternalProvider()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 5m, 100m);
        await AddPriceAsync(db, "2330", new DateOnly(2026, 1, 31), 115m, 110m);

        var report = await ReportEndpoints.GetStockPerformanceAsync(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            db,
            CreateTimeZoneService());

        Assert.Equal(new DateOnly(2026, 1, 1), report.DateStart);
        Assert.Equal(new DateOnly(2026, 1, 31), report.DateEnd);
        Assert.Equal("HistoricalRawClose", report.TerminalValuationSource);
        Assert.Equal(600m, report.Summary.CurrentGrossMarketValue);
        Assert.Equal(500m, report.Summary.RemainingCostBasis);
        Assert.Single(report.InstrumentBreakdown);
        Assert.NotNull(report.Xirr.Value);
        Assert.NotNull(report.LedgerCoverage.Value);
    }

    /// <summary>驗證績效 endpoint 載入 requestedStart 前 lookback 內的 raw close 供 XIRR 使用。</summary>
    [Fact]
    public async Task GetStockPerformance_LoadsPreStartRawCloseForOpeningXirr()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, new DateOnly(2025, 12, 1), 5m, 100m);
        await AddPriceAsync(db, "2330", new DateOnly(2025, 12, 15), 105m, 100m);
        await AddPriceAsync(db, "2330", new DateOnly(2026, 1, 31), 125m, 120m);

        var report = await ReportEndpoints.GetStockPerformanceAsync(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            db,
            CreateTimeZoneService());

        Assert.NotNull(report.Xirr.Value);
    }

    /// <summary>驗證 endpoint 的 lookback 價格不會進入 TWR 期間內觀測或改變其結果。</summary>
    [Fact]
    public async Task GetStockPerformance_LookbackPriceDoesNotChangeTwrPeriodObservations()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, new DateOnly(2025, 12, 1), 5m, 100m);
        await AddPriceAsync(db, "2330", new DateOnly(2025, 12, 15), 105m, 100m);
        await AddPriceAsync(db, "2330", new DateOnly(2026, 1, 1), 105m, 100m);
        await AddPriceAsync(db, "2330", new DateOnly(2026, 1, 31), 125m, 120m);

        var report = await ReportEndpoints.GetStockPerformanceAsync(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            db,
            CreateTimeZoneService());

        Assert.Equal(2, report.DataQuality.PriceObservationCount);
        Assert.InRange(report.Twr.Value!.Value, 0.199999d, 0.200001d);
    }

    /// <summary>驗證 requestedStart 前第 31 日的 raw close 仍納入 opening lookback。</summary>
    [Fact]
    public async Task GetStockPerformance_IncludesExactThirtyOneDayLookback()
    {
        await using var db = await CreateDbContextAsync();
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 1, 31);
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, start.AddDays(-40), 5m, 100m);
        await AddPriceAsync(db, "2330", start.AddDays(-31), 105m, 100m);
        await AddPriceAsync(db, "2330", end, 125m, 120m);

        var report = await ReportEndpoints.GetStockPerformanceAsync(
            start,
            end,
            db,
            CreateTimeZoneService());

        Assert.NotNull(report.Xirr.Value);
    }

    /// <summary>驗證 requestedStart 前第 32 日的 raw close 不會繞過 31 日 lookback 限制。</summary>
    [Fact]
    public async Task GetStockPerformance_ExcludesThirtyTwoDayLookback()
    {
        await using var db = await CreateDbContextAsync();
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 1, 31);
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, start.AddDays(-40), 5m, 100m);
        await AddPriceAsync(db, "2330", start.AddDays(-32), 105m, 100m);
        await AddPriceAsync(db, "2330", end, 125m, 120m);

        var report = await ReportEndpoints.GetStockPerformanceAsync(
            start,
            end,
            db,
            CreateTimeZoneService());

        Assert.Null(report.Xirr.Value);
        Assert.Equal(StockPerformanceUnavailableReason.MissingOpeningValue, report.Xirr.UnavailableReason);
    }

    /// <summary>驗證期初缺價時 endpoint 仍回傳報表並保留 MissingOpeningValue reason。</summary>
    [Fact]
    public async Task GetStockPerformance_MissingPreStartRawClose_ReturnsOkWithTypedReason()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, new DateOnly(2025, 12, 1), 5m, 100m);
        await AddPriceAsync(db, "2330", new DateOnly(2026, 1, 31), 125m, 120m);
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().GetAsync(
            "/api/reports/stock-performance?dateStart=2026-01-01&dateEnd=2026-01-31");

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var xirr = json.RootElement.GetProperty("xirr");
        Assert.Equal(JsonValueKind.Null, xirr.GetProperty("value").ValueKind);
        Assert.Equal("MissingOpeningValue", xirr.GetProperty("unavailableReason").GetString());
    }

    /// <summary>驗證 HTTP response 包含績效報表的所有頂層 JSON contract 欄位。</summary>
    [Fact]
    public async Task GetStockPerformance_ReturnsCompleteJsonContract()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, new DateOnly(2026, 1, 1), 5m, 100m);
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var response = await app.GetTestClient().GetAsync(
            "/api/reports/stock-performance?dateStart=2026-01-01&dateEnd=2026-01-31");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.True(root.TryGetProperty("periodStart", out _)
            || root.TryGetProperty("dateStart", out _));
        Assert.True(root.TryGetProperty("dateEnd", out _));
        Assert.True(root.TryGetProperty("trackingStartDate", out _));
        Assert.True(root.TryGetProperty("hasSyntheticOpeningBalances", out _));
        Assert.True(root.TryGetProperty("terminalValuationSource", out _));
        Assert.True(root.TryGetProperty("ledgerCoverage", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("twr", out _));
        Assert.True(root.TryGetProperty("xirr", out _));
        Assert.True(root.TryGetProperty("xirrOpeningValue", out _));
        Assert.True(root.TryGetProperty("xirrOpeningValuationSource", out _));
        Assert.True(root.TryGetProperty("monthlyPoints", out _));
        Assert.True(root.TryGetProperty("instrumentBreakdown", out _));
        Assert.True(root.TryGetProperty("dataQuality", out _));
    }

    /// <summary>驗證目前 period end 使用 Stock.CurrentPrice 而非要求外部行情。</summary>
    [Fact]
    public async Task GetStockPerformance_UsesCurrentPriceForCurrentPeriod()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        var today = CreateTimeZoneService().GetLocalDate();
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, today, 5m, 100m);

        var report = await ReportEndpoints.GetStockPerformanceAsync(
            today,
            today,
            db,
            CreateTimeZoneService());

        Assert.Equal("CurrentPrice", report.TerminalValuationSource);
        Assert.Equal(600m, report.Summary.CurrentGrossMarketValue);
    }

    /// <summary>驗證無持股時回傳安全 empty state 而不是 synthetic 零報酬。</summary>
    [Fact]
    public async Task GetStockPerformance_ReturnsEmptyStateWithoutSyntheticZero()
    {
        await using var db = await CreateDbContextAsync();

        var report = await ReportEndpoints.GetStockPerformanceAsync(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            db,
            CreateTimeZoneService());

        Assert.Equal(0m, report.Summary.CurrentGrossMarketValue);
        Assert.Null(report.LedgerCoverage.Value);
        Assert.Equal(StockPerformanceUnavailableReason.NoHoldings, report.LedgerCoverage.UnavailableReason);
        Assert.Null(report.Twr.Value);
        Assert.Null(report.Xirr.Value);
        Assert.Empty(report.MonthlyPoints);
        Assert.Empty(report.InstrumentBreakdown);
    }

    /// <summary>驗證 endpoint 拒絕 dateEnd 早於 dateStart 的無效日期範圍。</summary>
    [Fact]
    public async Task GetStockPerformance_RejectsInvalidDateRange()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var response = await app.GetTestClient().GetAsync(
            "/api/reports/stock-performance?dateStart=2026-02-01&dateEnd=2026-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InvalidDateRange", await response.Content.ReadAsStringAsync());
    }

    /// <summary>驗證 endpoint 拒絕晚於系統時區今天的 dateEnd，並維持 typed error contract。</summary>
    [Fact]
    public async Task GetStockPerformance_RejectsFutureDateEnd()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var response = await app.GetTestClient().GetAsync(
            "/api/reports/stock-performance?dateStart=2026-01-01&dateEnd=9999-12-31");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("InvalidDateRange", body);
        Assert.Contains("dateEnd 不可晚於今天", body);
    }

    /// <summary>驗證今天與歷史 dateEnd 都維持可計算的有效 request。</summary>
    [Theory]
    [InlineData("2026-08-28")]
    [InlineData("2026-01-31")]
    public async Task GetStockPerformance_AcceptsTodayAndHistoricalDateEnd(string dateEndValue)
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var response = await app.GetTestClient().GetAsync(
            $"/api/reports/stock-performance?dateStart=2026-01-01&dateEnd={dateEndValue}");

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(dateEndValue, json.RootElement.GetProperty("dateEnd").GetString());
    }

    /// <summary>驗證省略 dateEnd 時使用固定系統時區今天作為報表期末日。</summary>
    [Fact]
    public async Task GetStockPerformance_UsesLocalTodayWhenDateEndIsOmitted()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var response = await app.GetTestClient().GetAsync(
            "/api/reports/stock-performance?dateStart=2026-01-01");

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2026-08-28", json.RootElement.GetProperty("dateEnd").GetString());
    }

    /// <summary>驗證 all-time request 以最早 Ledger 交易日作為報表起點。</summary>
    [Fact]
    public async Task GetStockPerformance_AllTimeUsesTrackingStart()
    {
        await using var db = await CreateDbContextAsync();
        var stock = await AddStockAsync(db, 5m, 100m, 120m);
        await AddTransactionAsync(db, stock.Id, StockTransactionType.Buy, new DateOnly(2025, 12, 1), 5m, 100m);

        var report = await ReportEndpoints.GetStockPerformanceAsync(
            null,
            new DateOnly(2026, 1, 31),
            db,
            CreateTimeZoneService());

        Assert.Equal(new DateOnly(2025, 12, 1), report.DateStart);
        Assert.Equal(new DateOnly(2025, 12, 1), report.TrackingStartDate);
    }

    /// <summary>驗證績效路由要求既有 reports:read scope metadata。</summary>
    [Fact]
    public async Task MapReportEndpoints_StockPerformanceRequiresReportsReadScope()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/api/reports/stock-performance");
        var metadata = endpoint.Metadata.GetMetadata<ApiTokenScopeMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal(ApiTokenScopes.ReportsRead, metadata!.RequiredScope);
    }

    /// <summary>建立只映射報表端點且不註冊外部行情 provider 的測試 app。</summary>
    private static async Task<WebApplication> CreateReportAppAsync(SqliteConnection connection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddSingleton(CreateTimeZoneService());
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        app.MapReportEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>建立固定台灣時區設定的測試服務。</summary>
    private static TimeZoneService CreateTimeZoneService()
        => new(
            Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions()),
            new FixedTimeProvider(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)));

    /// <summary>提供固定 UTC 時間，讓 endpoint 日期 contract 測試不受執行環境影響。</summary>
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));

        /// <summary>回傳測試指定的 UTC instant。</summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    /// <summary>建立開啟中的 SQLite 記憶體資料庫並套用目前 schema。</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    /// <summary>建立測試用持股主檔。</summary>
    private static async Task<Stock> AddStockAsync(
        AppDbContext db,
        decimal shares,
        decimal buyPrice,
        decimal currentPrice)
    {
        var stock = new Stock
        {
            Name = "測試標的",
            Symbol = "2330",
            Market = StockMarket.Twse,
            Shares = shares,
            BuyPrice = buyPrice,
            CurrentPrice = currentPrice,
        };
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        return stock;
    }

    /// <summary>建立測試用原始 Ledger 交易。</summary>
    private static async Task AddTransactionAsync(
        AppDbContext db,
        int stockId,
        StockTransactionType type,
        DateOnly tradeDate,
        decimal shares,
        decimal price)
    {
        db.StockTransactions.Add(new StockTransaction
        {
            StockId = stockId,
            Type = type,
            TradeDate = tradeDate,
            Sequence = 1,
            Shares = shares,
            Price = price,
            Fee = 0m,
            Tax = 0m,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>建立測試用 adjusted 與 raw close 歷史價格。</summary>
    private static async Task AddPriceAsync(
        AppDbContext db,
        string symbol,
        DateOnly tradingDate,
        decimal adjustedClose,
        decimal close)
    {
        db.HistoricalAdjustedPrices.Add(new HistoricalAdjustedPrice
        {
            Market = StockMarket.Twse,
            Symbol = symbol,
            TradingDate = tradingDate,
            AdjustedClose = adjustedClose,
            Close = close,
            Provider = "fixture",
            FetchedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
