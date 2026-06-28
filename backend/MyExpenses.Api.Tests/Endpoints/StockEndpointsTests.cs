using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
            new Stock { Name = "台積電", Symbol = "2330", InstrumentType = StockInstrumentType.Stock, Shares = 1000m, BuyPrice = 1000m, CurrentPrice = 1050m, Broker = "測試券商" },
            new Stock { Name = "台灣50", Symbol = "0050", InstrumentType = StockInstrumentType.StockEtf, Shares = 1000m, BuyPrice = 40m, CurrentPrice = 50m, Broker = "測試券商" });
        await db.SaveChangesAsync();

        return db;
    }
}
