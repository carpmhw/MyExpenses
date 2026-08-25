using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class StockLedgerServiceTests
{
    /// <summary>驗證建立、修改與刪除交易都會在完整 replay 後更新 Stock projection。</summary>
    [Fact]
    public async Task Mutations_ReplayAndProjectStockAtomically()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var stock = await AddStockAsync(db, shares: 0m, buyPrice: 0m, currentPrice: 100m);
        var service = new StockLedgerService(db);

        var first = await service.CreateTransactionAsync(stock.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Buy,
            new DateOnly(2026, 1, 1),
            Shares: 10m,
            Price: 100m,
            Fee: 2m,
            Tax: 1m));
        var second = await service.CreateTransactionAsync(stock.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Buy,
            new DateOnly(2026, 1, 2),
            Shares: 10m,
            Price: 120m,
            Fee: 0m,
            Tax: 0m));

        Assert.Equal(10m, first.Replay.RemainingShares);
        Assert.Equal(110m, second.Replay.ExecutionAveragePrice);
        Assert.Equal(20m, stock.Shares);
        Assert.Equal(110m, stock.BuyPrice);

        var updated = await service.UpdateTransactionAsync(
            first.Transaction.Id,
            first.Transaction.StockId,
            new StockLedgerTransactionCommand(
                StockTransactionType.Buy,
                new DateOnly(2026, 1, 1),
                Shares: 10m,
                Price: 130m,
                Fee: 0m,
                Tax: 0m));
        Assert.Equal(125m, updated.Replay.ExecutionAveragePrice);

        await service.DeleteTransactionAsync(second.Transaction.Id);
        await db.Entry(stock).ReloadAsync();
        Assert.Equal(10m, stock.Shares);
        Assert.Equal(130m, stock.BuyPrice);
        Assert.Single(await db.StockTransactions.ToListAsync());
    }

    /// <summary>驗證 oversell 會 rollback transaction、交易資料與既有 projection。</summary>
    [Fact]
    public async Task CreateTransaction_Oversell_RollsBackWithoutPartialProjection()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var stock = await AddStockAsync(db, shares: 5m, buyPrice: 100m, currentPrice: 100m);
        var service = new StockLedgerService(db);
        var opening = await service.CreateTransactionAsync(stock.Id, new StockLedgerTransactionCommand(
            StockTransactionType.OpeningBalance,
            new DateOnly(2026, 1, 1),
            Shares: 5m,
            Price: 100m,
            OpeningMarketValue: 500m));

        var exception = await Assert.ThrowsAsync<InsufficientSharesException>(() =>
            service.CreateTransactionAsync(stock.Id, new StockLedgerTransactionCommand(
                StockTransactionType.Sell,
                new DateOnly(2026, 1, 2),
                Shares: 6m,
                Price: 120m)));

        Assert.Equal("InsufficientShares", exception.Code);
        Assert.Equal(1, await db.StockTransactions.CountAsync());
        await db.Entry(stock).ReloadAsync();
        Assert.Equal(5m, stock.Shares);
        Assert.Equal(100m, stock.BuyPrice);
        Assert.Equal(opening.Transaction.Id, (await db.StockTransactions.SingleAsync()).Id);
    }

    /// <summary>驗證修改歷史交易造成後續 oversell 時，原交易與 projection 都維持不變。</summary>
    [Fact]
    public async Task UpdateHistoricalTransaction_InvalidatesLaterSell_RollsBackHistory()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var stock = await AddStockAsync(db, shares: 0m, buyPrice: 0m, currentPrice: 100m);
        var service = new StockLedgerService(db);
        var buy = await service.CreateTransactionAsync(stock.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Buy,
            new DateOnly(2026, 1, 1),
            Shares: 10m,
            Price: 100m));
        await service.CreateTransactionAsync(stock.Id, new StockLedgerTransactionCommand(
            StockTransactionType.Sell,
            new DateOnly(2026, 1, 2),
            Shares: 8m,
            Price: 120m));

        await Assert.ThrowsAsync<InsufficientSharesException>(() =>
            service.UpdateTransactionAsync(
                buy.Transaction.Id,
                stock.Id,
                new StockLedgerTransactionCommand(
                    StockTransactionType.Buy,
                    new DateOnly(2026, 1, 1),
                    Shares: 5m,
                    Price: 100m)));

        var storedBuy = await db.StockTransactions.SingleAsync(transaction => transaction.Id == buy.Transaction.Id);
        Assert.Equal(10m, storedBuy.Shares);
        await db.Entry(stock).ReloadAsync();
        Assert.Equal(2m, stock.Shares);
    }

    /// <summary>驗證同日新增與異日修改會維持可預測的 sequence。</summary>
    [Fact]
    public async Task Mutations_AssignSameDaySequenceAndMoveDateToTheEnd()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var stock = await AddStockAsync(db, shares: 0m, buyPrice: 0m, currentPrice: 100m);
        var service = new StockLedgerService(db);
        var first = await service.CreateTransactionAsync(stock.Id, Buy(new DateOnly(2026, 1, 1), 1m, 100m));
        var second = await service.CreateTransactionAsync(stock.Id, Buy(new DateOnly(2026, 1, 1), 1m, 110m));
        var third = await service.CreateTransactionAsync(stock.Id, Buy(new DateOnly(2026, 1, 2), 1m, 120m));

        Assert.Equal(1, first.Transaction.Sequence);
        Assert.Equal(2, second.Transaction.Sequence);
        await service.UpdateTransactionAsync(
            first.Transaction.Id,
            stock.Id,
            Buy(new DateOnly(2026, 1, 2), 1m, 100m));

        Assert.Equal(
            [second.Transaction.Id, third.Transaction.Id, first.Transaction.Id],
            await db.StockTransactions
                .OrderBy(transaction => transaction.TradeDate)
                .ThenBy(transaction => transaction.Sequence)
                .Select(transaction => transaction.Id)
                .ToListAsync());
        Assert.Equal(2, (await db.StockTransactions.SingleAsync(transaction => transaction.Id == first.Transaction.Id)).Sequence);
    }

    /// <summary>驗證初始化會將所有 blocking stock 原子拒絕且不建立部分 baseline。</summary>
    [Fact]
    public async Task Initialize_WithBlockingStock_DoesNotCreatePartialBaseline()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        await AddStockAsync(db, shares: 10m, buyPrice: 100m, currentPrice: 120m, symbol: "GOOD");
        await AddStockAsync(db, shares: 20m, buyPrice: 0m, currentPrice: 120m, symbol: "BLOCKED");
        var service = new StockLedgerService(db);

        var response = await service.InitializeAsync(new StockLedgerInitializationCommand(new DateOnly(2026, 8, 25)));

        Assert.Equal(0, response.InitializedCount);
        Assert.Equal(1, response.BlockingCount);
        Assert.Contains(response.BlockingStocks, stock => stock.Symbol == "BLOCKED");
        Assert.Empty(await db.StockTransactions.ToListAsync());
    }

    /// <summary>驗證有效初始化使用既有成本與目前市值，且重複呼叫保持冪等。</summary>
    [Fact]
    public async Task Initialize_UsesExistingCostAndCurrentValueIdempotently()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var stock = await AddStockAsync(db, shares: 10m, buyPrice: 100m, currentPrice: 120m);
        var service = new StockLedgerService(db);

        var first = await service.InitializeAsync(new StockLedgerInitializationCommand(new DateOnly(2026, 8, 25)));
        var second = await service.InitializeAsync(new StockLedgerInitializationCommand(new DateOnly(2026, 8, 26)));
        var opening = await db.StockTransactions.SingleAsync();

        Assert.Equal(1, first.InitializedCount);
        Assert.Equal(1, second.SkippedCount);
        Assert.Equal(StockTransactionType.OpeningBalance, opening.Type);
        Assert.Equal(100m, opening.Price);
        Assert.Equal(1200m, opening.OpeningMarketValue);
        Assert.Equal(new DateOnly(2026, 8, 25), opening.TradeDate);
        Assert.Equal(10m, stock.Shares);
        Assert.Equal(100m, stock.BuyPrice);
    }

    /// <summary>驗證零股持股會跳過初始化並保留既有主檔資料。</summary>
    [Fact]
    public async Task Initialize_SkipsZeroShareStock()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var stock = await AddStockAsync(db, shares: 0m, buyPrice: 100m, currentPrice: 120m);
        var service = new StockLedgerService(db);

        var response = await service.InitializeAsync(new StockLedgerInitializationCommand(new DateOnly(2026, 8, 25)));

        Assert.Equal(1, response.SkippedCount);
        Assert.Empty(await db.StockTransactions.ToListAsync());
        Assert.Equal(0m, stock.Shares);
    }

    /// <summary>驗證已有任意 Ledger 的股票不會因不同 baseline date 重複初始化。</summary>
    [Fact]
    public async Task Initialize_SkipsExistingLedger()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var stock = await AddStockAsync(db, shares: 10m, buyPrice: 100m, currentPrice: 120m);
        var service = new StockLedgerService(db);
        await service.CreateTransactionAsync(stock.Id, Buy(new DateOnly(2026, 8, 1), 10m, 100m));

        var response = await service.InitializeAsync(new StockLedgerInitializationCommand(new DateOnly(2026, 8, 25)));

        Assert.Equal(1, response.SkippedCount);
        Assert.Single(await db.StockTransactions.ToListAsync());
    }

    /// <summary>驗證 atomic position command 同時建立 Stock 與第一筆 Ledger。</summary>
    [Fact]
    public async Task CreatePosition_CreatesStockAndLedgerAtomically()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var service = new StockLedgerService(db);

        var result = await service.CreatePositionAsync(new StockPositionCommand(
            "新標的",
            "2330",
            StockMarket.Twse,
            StockInstrumentType.Stock,
            10m,
            100m,
            120m,
            new DateOnly(2026, 8, 25),
            StockTransactionType.Buy));

        Assert.Equal("2330", result.Stock.Symbol);
        Assert.Equal(StockTransactionType.Buy, result.Transaction.Type);
        Assert.Equal(10m, result.Stock.Shares);
        Assert.Single(await db.StockTransactions.ToListAsync());
    }

    /// <summary>驗證 atomic position command 也能以目前市值建立 synthetic opening。</summary>
    [Fact]
    public async Task CreatePosition_WithOpeningBalance_UsesCurrentGrossValue()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var service = new StockLedgerService(db);

        var result = await service.CreatePositionAsync(new StockPositionCommand(
            "期初標的",
            "00679B",
            StockMarket.Tpex,
            StockInstrumentType.BondEtf,
            10m,
            100m,
            120m,
            new DateOnly(2026, 8, 25),
            StockTransactionType.OpeningBalance));

        Assert.Equal(1200m, result.Transaction.OpeningMarketValue);
        Assert.Equal(1000m, result.Replay.RemainingCostBasis);
        Assert.Equal(10m, result.Stock.Shares);
    }

    /// <summary>驗證 atomic position command 任一欄位失敗時不留下孤立 Stock。</summary>
    [Fact]
    public async Task CreatePosition_InvalidInitialTransaction_RollsBackStock()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbContextAsync(connection);
        var service = new StockLedgerService(db);

        await Assert.ThrowsAsync<StockLedgerException>(() =>
            service.CreatePositionAsync(new StockPositionCommand(
                "錯誤標的",
                "2330",
                StockMarket.Twse,
                StockInstrumentType.Stock,
                0m,
                100m,
                120m,
                new DateOnly(2026, 8, 25),
                StockTransactionType.Buy)));

        Assert.Empty(await db.Stocks.ToListAsync());
        Assert.Empty(await db.StockTransactions.ToListAsync());
    }

    /// <summary>建立測試用買入命令以保持 service 測試輸入一致。</summary>
    private static StockLedgerTransactionCommand Buy(DateOnly date, decimal shares, decimal price)
        => new(StockTransactionType.Buy, date, Shares: shares, Price: price);

    /// <summary>建立開啟中的 SQLite 記憶體連線。</summary>
    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>建立並初始化使用指定 SQLite 連線的 DbContext。</summary>
    private static async Task<AppDbContext> CreateDbContextAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    /// <summary>建立並保存測試用股票主檔。</summary>
    private static async Task<Stock> AddStockAsync(
        AppDbContext db,
        decimal shares,
        decimal buyPrice,
        decimal currentPrice,
        string symbol = "2330")
    {
        var stock = new Stock
        {
            Name = "測試標的",
            Symbol = symbol,
            Market = StockMarket.Twse,
            Shares = shares,
            BuyPrice = buyPrice,
            CurrentPrice = currentPrice,
        };
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        return stock;
    }
}
