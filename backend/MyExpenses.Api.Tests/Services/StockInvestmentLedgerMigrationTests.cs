using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class StockInvestmentLedgerMigrationTests
{
    /// <summary>驗證正式 Ledger migration 已註冊且不會替既有 adjusted history 偽造 raw close。</summary>
    [Fact]
    public async Task AddStockInvestmentLedger_PreservesExistingHistoryAndStartsEmpty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        Assert.Contains(
            db.Database.GetMigrations(),
            migration => migration.Contains("AddStockInvestmentLedger", StringComparison.Ordinal));

        await db.Database.MigrateAsync("20260806165736_AddStockMarketRiskData");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO HistoricalAdjustedPrices (Market, Symbol, TradingDate, AdjustedClose, Provider, FetchedAtUtc) "
            + "VALUES ('Twse', '2330', '2026-08-25', 100.000000, 'fixture', '2026-08-25 00:00:00')");

        await db.Database.MigrateAsync();

        var price = await db.HistoricalAdjustedPrices.SingleAsync();
        Assert.Equal(100m, price.AdjustedClose);
        Assert.Null(price.Close);
        Assert.Empty(await db.StockTransactions.ToListAsync());
    }

    /// <summary>以 file-backed SQLite legacy database 驗證 migration、初始化與 projection 可追溯。</summary>
    [Fact]
    public async Task LegacySqliteBackup_MigratesAndInitializesWithoutChangingExistingStockData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"myexpenses-ledger-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            await using var db = new AppDbContext(options);
            await db.Database.MigrateAsync("20260806165736_AddStockMarketRiskData");
            db.Stocks.Add(new Stock
            {
                Name = "既有持股",
                Symbol = "2330",
                Market = StockMarket.Twse,
                InstrumentType = StockInstrumentType.Stock,
                Shares = 10m,
                BuyPrice = 500m,
                CurrentPrice = 600m,
                Broker = "legacy",
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO HistoricalAdjustedPrices (Market, Symbol, TradingDate, AdjustedClose, Provider, FetchedAtUtc) "
                + "VALUES ('Twse', '2330', '2026-08-25', 600.000000, 'fixture', '2026-08-25 00:00:00')");

            await db.Database.MigrateAsync();

            var stock = await db.Stocks.SingleAsync();
            Assert.Equal(10m, stock.Shares);
            Assert.Equal(500m, stock.BuyPrice);
            Assert.Equal(600m, stock.CurrentPrice);
            Assert.Null(await db.HistoricalAdjustedPrices.Select(price => price.Close).SingleAsync());
            Assert.Empty(await db.StockTransactions.ToListAsync());

            var service = new StockLedgerService(db);
            var initialized = await service.InitializeAsync(new StockLedgerInitializationCommand(new DateOnly(2026, 8, 1)));
            Assert.Equal(1, initialized.InitializedCount);
            Assert.Equal(1, await db.StockTransactions.CountAsync());
            var opening = await db.StockTransactions.SingleAsync();
            Assert.Equal(6000m, opening.OpeningMarketValue);
            Assert.Equal(10m, (await db.Stocks.SingleAsync()).Shares);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
