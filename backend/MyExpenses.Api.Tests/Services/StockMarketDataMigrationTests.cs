using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class StockMarketDataMigrationTests
{
    /// <summary>驗證既有持股在新增市場欄位後仍保留且使用 Unknown 預設值。</summary>
    [Fact]
    public async Task Migration_PreservesExistingStocksAndDefaultsMarketToUnknown()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        await db.Database.MigrateAsync("20260802132902_AddSingleOwnerInvariant");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Stocks (Name, Symbol, InstrumentType, Shares, BuyPrice, CurrentPrice, Broker, LastPriceUpdate) " +
            "VALUES ('台積電', '2330', 'Stock', 10, 500, 600, '測試券商', NULL)");

        await db.Database.MigrateAsync();

        var stock = await db.Database.SqlQueryRaw<LegacyStockRow>(
            "SELECT Name, Symbol, InstrumentType, Shares, BuyPrice, CurrentPrice, Broker, Market FROM Stocks")
            .SingleAsync();

        Assert.Equal("台積電", stock.Name);
        Assert.Equal("2330", stock.Symbol);
        Assert.Equal("Stock", stock.InstrumentType);
        Assert.Equal(10m, stock.Shares);
        Assert.Equal(500m, stock.BuyPrice);
        Assert.Equal(600m, stock.CurrentPrice);
        Assert.Equal("測試券商", stock.Broker);
        Assert.Equal("Unknown", stock.Market);
    }

    /// <summary>驗證歷史價格與同步狀態使用必要欄位及複合唯一索引。</summary>
    [Fact]
    public async Task Migration_CreatesMarketDataTablesAndUniqueIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        await db.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(db, "HistoricalAdjustedPrices"));
        Assert.True(await TableExistsAsync(db, "HistoricalPriceSyncStates"));
        Assert.True(await IndexExistsAsync(db, "IX_HistoricalAdjustedPrices_Market_Symbol_TradingDate"));
        Assert.True(await IndexExistsAsync(db, "IX_HistoricalPriceSyncStates_Market_Symbol"));
        Assert.Equal(
            "TEXT",
            await GetColumnTypeAsync(db, "Stocks", "Market"));
        Assert.Equal(
            "TEXT",
            await GetColumnTypeAsync(db, "HistoricalAdjustedPrices", "Market"));
        Assert.Equal(
            "decimal(18,6)",
            await GetColumnTypeAsync(db, "HistoricalAdjustedPrices", "AdjustedClose"));
        Assert.Equal(
            "decimal(18,6)",
            await GetColumnTypeAsync(db, "HistoricalAdjustedPrices", "Close"));
    }

    /// <summary>驗證歷史價格不接受重複身分或非正值。</summary>
    [Fact]
    public async Task HistoricalAdjustedPriceSchema_RejectsDuplicateIdentityAndNonPositivePrice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO HistoricalAdjustedPrices (Market, Symbol, TradingDate, AdjustedClose, Provider, FetchedAtUtc) " +
            "VALUES ('Twse', '2330', '2026-08-01', 100, 'fixture', '2026-08-02 00:00:00')");

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO HistoricalAdjustedPrices (Market, Symbol, TradingDate, AdjustedClose, Provider, FetchedAtUtc) " +
            "VALUES ('Twse', '2330', '2026-08-01', 101, 'fixture', '2026-08-02 00:00:00')"));

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO HistoricalAdjustedPrices (Market, Symbol, TradingDate, AdjustedClose, Provider, FetchedAtUtc) " +
            "VALUES ('Twse', '2331', '2026-08-01', 0, 'fixture', '2026-08-02 00:00:00')"));

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO HistoricalAdjustedPrices (Market, Symbol, TradingDate, AdjustedClose, Close, Provider, FetchedAtUtc) " +
            "VALUES ('Twse', '2332', '2026-08-01', 100, 0, 'fixture', '2026-08-02 00:00:00')"));
    }

    /// <summary>驗證回滾到前一版 migration 會移除衍生的市場行情 schema。</summary>
    [Fact]
    public async Task Migration_DownRemovesMarketDataSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync("20260802132902_AddSingleOwnerInvariant");

        Assert.False(await TableExistsAsync(db, "HistoricalAdjustedPrices"));
        Assert.False(await TableExistsAsync(db, "HistoricalPriceSyncStates"));
        Assert.Null(await GetColumnTypeAsync(db, "Stocks", "Market"));
    }

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>查詢 SQLite 是否存在指定資料表。</summary>
    private static async Task<bool> TableExistsAsync(AppDbContext db, string tableName)
        => await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}",
            tableName).SingleAsync() == 1;

    /// <summary>查詢 SQLite 是否存在指定索引。</summary>
    private static async Task<bool> IndexExistsAsync(AppDbContext db, string indexName)
        => await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = {0}",
            indexName).SingleAsync() == 1;

    /// <summary>讀取 SQLite 欄位型別，供 schema contract 驗證使用。</summary>
    private static async Task<string?> GetColumnTypeAsync(AppDbContext db, string tableName, string columnName)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info([{tableName}])";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == columnName)
                return reader.GetString(2);
        }

        return null;
    }

    /// <summary>承接 migration 後既有持股欄位的最小查詢結果。</summary>
    private sealed class LegacyStockRow
    {
        public string Name { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string InstrumentType { get; set; } = string.Empty;
        public decimal Shares { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public string? Broker { get; set; }
        public string Market { get; set; } = string.Empty;
    }
}
