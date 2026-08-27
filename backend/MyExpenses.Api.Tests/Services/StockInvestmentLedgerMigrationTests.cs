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

    /// <summary>驗證股票股利 migration 保留既有交易的識別、欄位、稽核時間、索引與外鍵。</summary>
    [Fact]
    public async Task AddStockDividendTransaction_PreservesExistingLedgerRowsAndRelations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"myexpenses-stock-dividend-legacy-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            List<StockTransaction> expected;
            await using (var legacyDb = new AppDbContext(options))
            {
                Assert.Contains(
                    legacyDb.Database.GetMigrations(),
                    migration => migration.Contains("AddStockDividendTransaction", StringComparison.Ordinal));
                await legacyDb.Database.MigrateAsync("20260825130313_AddStockInvestmentLedger");
                var stock = new Stock
                {
                    Name = "既有 Ledger",
                    Symbol = "2330",
                    Market = StockMarket.Twse,
                    InstrumentType = StockInstrumentType.Stock,
                    Shares = 110m,
                    BuyPrice = 100m,
                    CurrentPrice = 110m,
                    Broker = "legacy",
                };
                legacyDb.Stocks.Add(stock);
                await legacyDb.SaveChangesAsync();
                var createdAt = new DateTime(2026, 8, 25, 1, 2, 3, DateTimeKind.Utc);
                legacyDb.StockTransactions.AddRange(
                    new StockTransaction
                    {
                        StockId = stock.Id,
                        Type = StockTransactionType.OpeningBalance,
                        TradeDate = new DateOnly(2026, 1, 1),
                        Sequence = 1,
                        Shares = 100m,
                        Price = 90m,
                        Fee = 0m,
                        Tax = 0m,
                        OpeningMarketValue = 9000m,
                        Notes = "legacy opening",
                        CreatedAtUtc = createdAt,
                        UpdatedAtUtc = createdAt,
                    },
                    new StockTransaction
                    {
                        StockId = stock.Id,
                        Type = StockTransactionType.Buy,
                        TradeDate = new DateOnly(2026, 1, 2),
                        Sequence = 1,
                        Shares = 20m,
                        Price = 100m,
                        Fee = 2m,
                        Tax = 1m,
                        Notes = "legacy buy",
                        CreatedAtUtc = createdAt.AddMinutes(1),
                        UpdatedAtUtc = createdAt.AddMinutes(2),
                    },
                    new StockTransaction
                    {
                        StockId = stock.Id,
                        Type = StockTransactionType.Sell,
                        TradeDate = new DateOnly(2026, 1, 3),
                        Sequence = 1,
                        Shares = 10m,
                        Price = 120m,
                        Fee = 1m,
                        Tax = 2m,
                        Notes = "legacy sell",
                        CreatedAtUtc = createdAt.AddMinutes(3),
                        UpdatedAtUtc = createdAt.AddMinutes(4),
                    },
                    new StockTransaction
                    {
                        StockId = stock.Id,
                        Type = StockTransactionType.Dividend,
                        TradeDate = new DateOnly(2026, 1, 4),
                        Sequence = 1,
                        Fee = 1m,
                        Tax = 0m,
                        CashAmount = 50m,
                        Notes = "legacy dividend",
                        CreatedAtUtc = createdAt.AddMinutes(5),
                        UpdatedAtUtc = createdAt.AddMinutes(6),
                    });
                await legacyDb.SaveChangesAsync();
                expected = await legacyDb.StockTransactions.AsNoTracking().OrderBy(transaction => transaction.Id).ToListAsync();
            }

            await using var db = new AppDbContext(options);
            await db.Database.MigrateAsync();
            var actual = await db.StockTransactions.AsNoTracking().OrderBy(transaction => transaction.Id).ToListAsync();

            Assert.Equal(expected.Count, actual.Count);
            for (var index = 0; index < expected.Count; index++)
            {
                Assert.Equal(expected[index].Id, actual[index].Id);
                Assert.Equal(expected[index].StockId, actual[index].StockId);
                Assert.Equal(expected[index].Type, actual[index].Type);
                Assert.Equal(expected[index].TradeDate, actual[index].TradeDate);
                Assert.Equal(expected[index].Sequence, actual[index].Sequence);
                Assert.Equal(expected[index].Shares, actual[index].Shares);
                Assert.Equal(expected[index].Price, actual[index].Price);
                Assert.Equal(expected[index].Fee, actual[index].Fee);
                Assert.Equal(expected[index].Tax, actual[index].Tax);
                Assert.Equal(expected[index].CashAmount, actual[index].CashAmount);
                Assert.Equal(expected[index].OpeningMarketValue, actual[index].OpeningMarketValue);
                Assert.Equal(expected[index].Notes, actual[index].Notes);
                Assert.Equal(expected[index].CreatedAtUtc, actual[index].CreatedAtUtc);
                Assert.Equal(expected[index].UpdatedAtUtc, actual[index].UpdatedAtUtc);
            }

            var indexNames = await ReadPragmaIndexNamesAsync(db, "StockTransactions");
            Assert.Contains("IX_StockTransactions_StockId_TradeDate_Sequence", indexNames);
            Assert.Contains("IX_StockTransactions_StockId_Type_TradeDate", indexNames);
            Assert.Single(db.Model.FindEntityType(typeof(StockTransaction))!.GetForeignKeys());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>驗證資料庫 constraint 接受合法股票股利並拒絕所有禁止欄位矩陣。</summary>
    [Fact]
    public async Task AddStockDividendTransaction_EnforcesStockDividendTypeFields()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        var stock = new Stock
        {
            Name = "測試股票股利",
            Symbol = "2330",
            Market = StockMarket.Twse,
            InstrumentType = StockInstrumentType.Stock,
            CurrentPrice = 100m,
        };
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();

        db.StockTransactions.Add(new StockTransaction
        {
            StockId = stock.Id,
            Type = StockTransactionType.StockDividend,
            TradeDate = new DateOnly(2026, 1, 1),
            Sequence = 1,
            Shares = 10m,
            Fee = 0m,
            Tax = 0m,
        });
        await db.SaveChangesAsync();

        var invalidTransactions = new StockTransaction[]
        {
            new() { Shares = null },
            new() { Shares = 0m },
            new() { Shares = -1m },
            new() { Shares = 1m, Price = 100m },
            new() { Shares = 1m, CashAmount = 100m },
            new() { Shares = 1m, OpeningMarketValue = 100m },
            new() { Shares = 1m, Fee = 1m },
            new() { Shares = 1m, Tax = 1m },
        };

        for (var index = 0; index < invalidTransactions.Length; index++)
        {
            var transaction = invalidTransactions[index];
            transaction.StockId = stock.Id;
            transaction.Type = StockTransactionType.StockDividend;
            transaction.TradeDate = new DateOnly(2026, 2, index + 1);
            transaction.Sequence = 1;
            db.StockTransactions.Add(transaction);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
        }

        Assert.Single(await db.StockTransactions.ToListAsync());
    }

    /// <summary>讀取 SQLite table index 名稱，供 migration 保留性測試核對實際 schema。</summary>
    private static async Task<IReadOnlyList<string>> ReadPragmaIndexNamesAsync(AppDbContext db, string tableName)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA index_list('{tableName}')";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(1));
        return names;
    }
}
