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
using Microsoft.Extensions.Options;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class StockStructureReportEndpointTests
{
    /// <summary>驗證持股結構端點套用篩選並保留全域篩選選項。</summary>
    [Fact]
    public async Task GetStockStructure_AppliesFiltersAndKeepsGlobalOptions()
    {
        await using var db = await CreateDbContextAsync();

        var report = await ReportEndpoints.GetStockStructureAsync(
            db,
            broker: " 甲券商 ",
            instrumentType: StockInstrumentType.Stock);

        var holding = Assert.Single(report.Holdings);
        Assert.Equal("AAA", holding.Symbol);
        Assert.Equal(2, report.AvailableBrokers.Count);
        Assert.Contains("甲券商", report.AvailableBrokers);
        Assert.Contains("乙券商", report.AvailableBrokers);
        Assert.Contains(StockInstrumentType.Stock, report.AvailableInstrumentTypes);
        Assert.Contains(StockInstrumentType.StockEtf, report.AvailableInstrumentTypes);
    }

    /// <summary>驗證持股結構報表路由要求 reports:read scope。</summary>
    [Fact]
    public async Task MapReportEndpoints_StockStructureRoutesRequireReportsReadScope()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/api/reports/stock-structure");

        var metadata = endpoint.Metadata.GetMetadata<ApiTokenScopeMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal(ApiTokenScopes.ReportsRead, metadata!.RequiredScope);

        var trendEndpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/api/reports/stock-value-trend");
        var trendMetadata = trendEndpoint.Metadata.GetMetadata<ApiTokenScopeMetadata>();
        Assert.NotNull(trendMetadata);
        Assert.Equal(ApiTokenScopes.ReportsRead, trendMetadata!.RequiredScope);
    }

    /// <summary>驗證股票價值趨勢依系統時區取每月最新快照且不補造月份。</summary>
    [Fact]
    public async Task GetStockValueTrend_UsesLatestLocalMonthSnapshotAndOmitsMissingMonths()
    {
        await using var db = await CreateDbContextAsync();
        db.SnapshotBatches.AddRange(
            CreateSnapshot(1, "一月早期", new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc), 100m, NetWorthBasis.AssetsOnly),
            CreateSnapshot(2, "一月晚期", new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc), 200m, NetWorthBasis.AssetsMinusLiabilities),
            CreateSnapshot(3, "三月", new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), 300m, NetWorthBasis.AssetsOnly),
            CreateSnapshot(4, "六月", new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), 600m, NetWorthBasis.AssetsMinusLiabilities));
        await db.SaveChangesAsync();
        var timeZoneService = new TimeZoneService(
            Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions()));

        var points = await ReportEndpoints.GetStockValueTrendAsync(
            6,
            db,
            timeZoneService,
            asOfDate: new DateOnly(2026, 6, 30));

        Assert.Equal(new[] { "2026/01", "2026/03", "2026/06" }, points.Select(point => point.Month));
        Assert.Equal(200m, points[0].TotalStockValue);
        Assert.Equal("一月晚期", points[0].Name);
        Assert.Equal(NetWorthBasis.AssetsMinusLiabilities, points[0].Basis);
        Assert.Equal(600m, points[^1].TotalStockValue);
    }

    /// <summary>驗證股票價值趨勢拒絕不支援的月份數。</summary>
    [Fact]
    public async Task GetStockValueTrend_RejectsUnsupportedMonthCount()
    {
        await using var db = await CreateDbContextAsync();
        var timeZoneService = new TimeZoneService(
            Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions()));

        await Assert.ThrowsAsync<ArgumentException>(() => ReportEndpoints.GetStockValueTrendAsync(
            0,
            db,
            timeZoneService,
            asOfDate: new DateOnly(2026, 6, 30)));
    }

    /// <summary>建立股票價值快照測試資料。</summary>
    private static SnapshotBatch CreateSnapshot(
        int id,
        string name,
        DateTime snapshotDate,
        decimal totalStockValue,
        NetWorthBasis basis)
    {
        return new SnapshotBatch
        {
            Id = id,
            Name = name,
            SnapshotDate = snapshotDate,
            TotalAssets = totalStockValue,
            TotalStockValue = totalStockValue,
            TotalStockCost = totalStockValue,
            TotalLiabilities = basis == NetWorthBasis.AssetsMinusLiabilities ? 0m : null,
            TotalNetWorth = totalStockValue,
            NetWorthBasis = basis,
        };
    }

    /// <summary>建立只映射報表端點的測試應用程式。</summary>
    private static async Task<WebApplication> CreateReportAppAsync(SqliteConnection connection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddSingleton(new TimeZoneService(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions())));

        var app = builder.Build();
        app.MapReportEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>建立包含不同券商與商品類型的 SQLite 測試資料庫。</summary>
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
            new Stock
            {
                Name = "標的一",
                Symbol = "AAA",
                InstrumentType = StockInstrumentType.Stock,
                Shares = 100m,
                BuyPrice = 80m,
                CurrentPrice = 100m,
                Broker = "甲券商",
            },
            new Stock
            {
                Name = "標的二",
                Symbol = "BBB",
                InstrumentType = StockInstrumentType.StockEtf,
                Shares = 100m,
                BuyPrice = 80m,
                CurrentPrice = 100m,
                Broker = "乙券商",
            });
        await db.SaveChangesAsync();
        return db;
    }
}
