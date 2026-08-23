using System.Text.Json;
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

public sealed class StockMarketRiskEndpointTests
{
    /// <summary>驗證端點預設 12 個月且拒絕未支援觀察期。</summary>
    [Fact]
    public async Task GetStockMarketRisk_UsesDefaultPeriodAndRejectsUnsupportedPeriod()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var defaultResponse = await client.GetAsync("/api/reports/stock-market-risk");
        defaultResponse.EnsureSuccessStatusCode();
        using var defaultJson = JsonDocument.Parse(await defaultResponse.Content.ReadAsStringAsync());
        Assert.Equal(12, defaultJson.RootElement.GetProperty("periodMonths").GetInt32());

        var invalidResponse = await client.GetAsync("/api/reports/stock-market-risk?periodMonths=4");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidResponse.StatusCode);
    }

    /// <summary>驗證沒有目前持股時回傳成功空狀態而不是零風險。</summary>
    [Fact]
    public async Task GetStockMarketRisk_ReturnsEmptyStateWithoutSyntheticZero()
    {
        await using var db = await CreateDbContextAsync();
        var result = await ReportEndpoints.GetStockMarketRiskAsync(
            3,
            db,
            CreateTimeZoneService(),
            new DateOnly(2026, 8, 7));

        Assert.Equal(3, result.PeriodMonths);
        Assert.Empty(result.IncludedInstruments);
        Assert.Equal(StockMarketRiskUnavailableReason.NoHoldings,
            result.PortfolioAnnualizedVolatility.UnavailableReason);
        Assert.Null(result.PortfolioAnnualizedVolatility.Value);
        Assert.Null(result.PortfolioMaximumDrawdown.Value);
        Assert.Equal(StockMarketRiskUnavailableReason.NoHoldings,
            result.PortfolioMaximumDrawdown.UnavailableReason);
        Assert.Equal(0d, result.EligibleMarketValueCoverage);
        Assert.Null(result.EligibleMarketValueCoverageMetric.Value);
        Assert.Equal(StockMarketRiskUnavailableReason.NoHoldings,
            result.EligibleMarketValueCoverageMetric.UnavailableReason);
        Assert.Empty(result.RiskContributions);
    }

    /// <summary>驗證 endpoint 只讀本機行情並返回截止日、同步警告與完整統計。</summary>
    [Fact]
    public async Task GetStockMarketRisk_ReadsLocalPricesAndReturnsWarnings()
    {
        await using var db = await CreateDbContextAsync();
        db.Stocks.Add(new Stock
        {
            Name = "台積電",
            Symbol = "2330",
            Market = StockMarket.Twse,
            InstrumentType = StockInstrumentType.Stock,
            Shares = 10m,
            BuyPrice = 90m,
            CurrentPrice = 100m,
        });
        foreach (var index in Enumerable.Range(0, 60))
        {
            db.HistoricalAdjustedPrices.Add(new HistoricalAdjustedPrice
            {
                Market = StockMarket.Twse,
                Symbol = "2330",
                TradingDate = new DateOnly(2026, 1, 1).AddDays(index),
                AdjustedClose = 100m + index,
                Provider = "fixture",
                FetchedAtUtc = DateTime.UtcNow,
            });
        }
        db.HistoricalAdjustedPrices.Add(new HistoricalAdjustedPrice
        {
            Market = StockMarket.Twse,
            Symbol = "UNRELATED",
            TradingDate = new DateOnly(2026, 8, 7),
            AdjustedClose = 100m,
            Provider = "fixture",
            FetchedAtUtc = DateTime.UtcNow,
        });
        db.HistoricalPriceSyncStates.Add(new HistoricalPriceSyncState
        {
            Market = StockMarket.Twse,
            Symbol = "2330",
            LastAttemptedAtUtc = DateTime.UtcNow,
            LastSucceededAtUtc = DateTime.UtcNow.AddDays(-1),
            LatestTradingDate = new DateOnly(2026, 3, 1),
            Status = HistoricalPriceSyncStatus.ProviderError,
            SafeMessage = "保留最後成功資料",
        });
        await db.SaveChangesAsync();

        var result = await ReportEndpoints.GetStockMarketRiskAsync(
            3,
            db,
            CreateTimeZoneService(),
            new DateOnly(2026, 8, 7));

        Assert.Equal(new DateOnly(2026, 3, 1), result.DataCutoffDate);
        Assert.Single(result.IncludedInstruments);
        Assert.NotNull(result.PortfolioAnnualizedVolatility.Value);
        Assert.NotNull(result.PortfolioMaximumDrawdown.Value);
        Assert.Single(result.RiskContributions);
        Assert.Single(result.SyncWarnings);
        Assert.Equal("保留最後成功資料", result.SyncWarnings[0].SafeMessage);
    }

    /// <summary>驗證市場風險路由 metadata 要求 reports:read scope。</summary>
    [Fact]
    public async Task MapReportEndpoints_StockMarketRiskRequiresReportsReadScope()
    {
        await using var db = await CreateDbContextAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());

        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/api/reports/stock-market-risk");
        var metadata = endpoint.Metadata.GetMetadata<ApiTokenScopeMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal(ApiTokenScopes.ReportsRead, metadata!.RequiredScope);
    }

    /// <summary>驗證市場風險 HTTP JSON 以增量欄位提供最大回撤與風險貢獻。</summary>
    [Fact]
    public async Task GetStockMarketRisk_ReturnsMaximumDrawdownAndRiskContributionsJsonFields()
    {
        await using var db = await CreateDbContextAsync();
        db.Stocks.Add(new Stock
        {
            Name = "台積電",
            Symbol = "2330",
            Market = StockMarket.Twse,
            InstrumentType = StockInstrumentType.Stock,
            Shares = 10m,
            BuyPrice = 90m,
            CurrentPrice = 100m,
        });
        foreach (var index in Enumerable.Range(0, 60))
        {
            db.HistoricalAdjustedPrices.Add(new HistoricalAdjustedPrice
            {
                Market = StockMarket.Twse,
                Symbol = "2330",
                TradingDate = new DateOnly(2026, 1, 1).AddDays(index),
                AdjustedClose = 100m + index,
                Provider = "fixture",
                FetchedAtUtc = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        await using var app = await CreateReportAppAsync((SqliteConnection)db.Database.GetDbConnection());
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/reports/stock-market-risk?periodMonths=3");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(json.RootElement.TryGetProperty("portfolioMaximumDrawdown", out var maximumDrawdown));
        Assert.Equal(JsonValueKind.Object, maximumDrawdown.ValueKind);
        Assert.True(maximumDrawdown.TryGetProperty("value", out _));
        Assert.True(maximumDrawdown.TryGetProperty("unavailableReason", out _));
        Assert.True(json.RootElement.TryGetProperty("riskContributions", out var riskContributions));
        Assert.Equal(JsonValueKind.Array, riskContributions.ValueKind);
        var contribution = Assert.Single(riskContributions.EnumerateArray());
        Assert.True(contribution.TryGetProperty("name", out _));
        Assert.True(contribution.TryGetProperty("symbol", out _));
        Assert.True(contribution.TryGetProperty("market", out _));
        Assert.True(contribution.TryGetProperty("grossMarketValue", out _));
        Assert.True(contribution.TryGetProperty("weight", out _));
        Assert.True(contribution.TryGetProperty("componentVolatilityContribution", out _));
        Assert.True(contribution.TryGetProperty("contributionPercentage", out _));
        Assert.False(contribution.TryGetProperty("marketValueWeight", out _));
        Assert.True(json.RootElement.TryGetProperty("eligibleMarketValueCoverageMetric", out var coverageMetric));
        Assert.Equal(JsonValueKind.Object, coverageMetric.ValueKind);
        Assert.True(coverageMetric.TryGetProperty("value", out _));
        Assert.True(coverageMetric.TryGetProperty("unavailableReason", out _));
    }

    /// <summary>建立只映射報表端點的測試 app，故意不註冊任何外部 provider。</summary>
    private static async Task<WebApplication> CreateReportAppAsync(SqliteConnection connection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddSingleton(CreateTimeZoneService());

        var app = builder.Build();
        app.MapReportEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>建立固定台灣時區設定的測試服務。</summary>
    private static TimeZoneService CreateTimeZoneService()
        => new(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions()));

    /// <summary>建立已建立 schema 的空 SQLite 測試資料庫。</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
