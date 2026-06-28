using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Endpoints;

public class ReportEndpointsTests
{
    /// <summary>Verifies net-worth reporting uses stock estimated net sell values for stock assets.</summary>
    [Fact]
    public async Task GetNetWorth_UsesStockEstimatedNetSellValue()
    {
        await using var db = await CreateDbContextAsync();

        var result = await ReportEndpoints.GetNetWorthAsync(db);

        Assert.Equal(1047432m, result.TotalAssets);
        Assert.Equal(0m, result.TotalLiabilities);
        Assert.Equal(1047432m, result.NetWorth);

        var stock = Assert.Single(result.Stocks);
        Assert.Equal(StockInstrumentType.Stock, stock.InstrumentType);
        Assert.Equal(1050000m, stock.GrossMarketValue);
        Assert.Equal(1046432m, stock.EstimatedNetSellValue);
    }

    /// <summary>Creates a SQLite-backed context for net-worth report tests.</summary>
    private static async Task<AppDbContext> CreateDbContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.BankAccounts.Add(new BankAccount { BankName = "測試銀行", AccountNumber = "12345", Balance = 1000m, AccountType = "活期" });
        db.Stocks.Add(new Stock { Name = "台積電", Symbol = "2330", InstrumentType = StockInstrumentType.Stock, Shares = 1000m, BuyPrice = 1000m, CurrentPrice = 1050m });
        await db.SaveChangesAsync();

        return db;
    }
}
