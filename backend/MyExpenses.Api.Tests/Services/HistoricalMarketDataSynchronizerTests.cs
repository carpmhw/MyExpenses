using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class HistoricalMarketDataSynchronizerTests
{
    /// <summary>驗證跨券商持股只同步唯一標的並讓未知市場取得唯一有效辨識。</summary>
    [Fact]
    public async Task SyncAsync_DeduplicatesHoldingsAndDetectsSingleValidMarket()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("台積電", "2330", StockMarket.Unknown, "甲券商"),
            CreateStock("台積電", " 2330 ", StockMarket.Unknown, "乙券商"),
            CreateStock("債券 ETF", "00679B", StockMarket.Tpex, "丙券商"),
            CreateStock("無代號", "   ", StockMarket.Unknown, "丁券商"));
        await db.SaveChangesAsync();

        var provider = new FakeProvider((market, symbol, _, _) =>
        {
            if (symbol == "2330" && market == StockMarket.Twse)
                return Success(symbol, "2330.TW", 100m);
            if (symbol == "00679B" && market == StockMarket.Tpex)
                return Success(symbol, "00679B.TWO", 50m);
            throw new HistoricalPriceProviderException("no_data", "沒有可用行情");
        });
        var synchronizer = new HistoricalMarketDataSynchronizer(db, provider);

        await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));

        var stocks = await db.Stocks.OrderBy(stock => stock.Id).ToListAsync();
        Assert.Equal(StockMarket.Twse, stocks[0].Market);
        Assert.Equal(StockMarket.Twse, stocks[1].Market);
        Assert.Equal(StockMarket.Tpex, stocks[2].Market);
        Assert.Equal(StockMarket.Unknown, stocks[3].Market);
        Assert.Equal(2, await db.HistoricalAdjustedPrices.CountAsync());
        Assert.Equal(3, provider.Requests.Count);
        Assert.All(provider.Requests, request =>
        {
            Assert.Equal(new DateOnly(2025, 7, 7), request.StartDate);
            Assert.Equal(new DateOnly(2026, 8, 7), request.EndDate);
        });
    }

    /// <summary>驗證相同交易日的重新同步會更新而非新增重複歷史價格。</summary>
    [Fact]
    public async Task SyncAsync_UpsertsRevisedTradingDatesIdempotently()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("台積電", "2330", StockMarket.Twse, "甲券商"));
        await db.SaveChangesAsync();
        db.HistoricalAdjustedPrices.Add(new HistoricalAdjustedPrice
        {
            Market = StockMarket.Twse,
            Symbol = "2330",
            TradingDate = new DateOnly(2026, 8, 6),
            AdjustedClose = 100m,
            Provider = "old",
            FetchedAtUtc = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var provider = new FakeProvider((_, _, _, _) => Success("2330", "2330.TW", 110m));
        var synchronizer = new HistoricalMarketDataSynchronizer(db, provider);

        await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));
        await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));

        var prices = await db.HistoricalAdjustedPrices.ToListAsync();
        var revised = Assert.Single(prices, price => price.TradingDate == new DateOnly(2026, 8, 6));
        Assert.Equal(110m, revised.AdjustedClose);
        Assert.Equal("YahooChart", revised.Provider);
        Assert.Single(prices);
    }

    /// <summary>驗證不再持有的標的不會繼續呼叫 provider 且既有歷史仍保留。</summary>
    [Fact]
    public async Task SyncAsync_StopsUpdatingDeletedHoldingsButRetainsHistory()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.HistoricalAdjustedPrices.Add(new HistoricalAdjustedPrice
        {
            Market = StockMarket.Twse,
            Symbol = "2330",
            TradingDate = new DateOnly(2026, 8, 6),
            AdjustedClose = 100m,
            Provider = "old",
            FetchedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) => Success("2330", "2330.TW", 110m));

        await new HistoricalMarketDataSynchronizer(db, provider)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Empty(provider.Requests);
        Assert.Equal(100m, await db.HistoricalAdjustedPrices.Select(price => price.AdjustedClose).SingleAsync());
    }

    /// <summary>驗證兩個市場候選都有效時會保留 Unknown 並標示需要使用者選擇。</summary>
    [Fact]
    public async Task SyncAsync_KeepsUnknownWhenBothMarketCandidatesAreValid()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("雙重代號", "9999", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) =>
            Success(symbol, market == StockMarket.Twse ? "9999.TW" : "9999.TWO", 10m));

        await new HistoricalMarketDataSynchronizer(db, provider)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(StockMarket.Unknown, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        var state = await db.HistoricalPriceSyncStates.SingleAsync();
        Assert.Equal(StockMarket.Unknown, state.Market);
        Assert.Equal(HistoricalPriceSyncStatus.AmbiguousMarket, state.Status);
        Assert.Empty(await db.HistoricalAdjustedPrices.ToListAsync());
    }

    /// <summary>驗證兩個市場候選都失敗時不修改未知市場或既有歷史。</summary>
    [Fact]
    public async Task SyncAsync_KeepsUnknownWhenNoMarketCandidateIsValid()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("未知代號", "8888", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new HistoricalPriceProviderException("no_data", "沒有可用行情"));

        await new HistoricalMarketDataSynchronizer(db, provider)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(StockMarket.Unknown, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        var state = await db.HistoricalPriceSyncStates.SingleAsync();
        Assert.Equal(HistoricalPriceSyncStatus.NoData, state.Status);
        Assert.Empty(await db.HistoricalAdjustedPrices.ToListAsync());
    }

    /// <summary>驗證另一市場發生服務錯誤時不會把單一成功候選誤判成唯一市場。</summary>
    [Fact]
    public async Task SyncAsync_DoesNotDetectMarketWhenOtherCandidateHasProviderError()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("不完整辨識", "7777", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) =>
        {
            if (market == StockMarket.Twse)
                return Success(symbol, "7777.TW", 10m);
            throw new HistoricalPriceProviderException("http_error", "歷史行情服務暫時無法使用");
        });

        await new HistoricalMarketDataSynchronizer(db, provider)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(StockMarket.Unknown, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError,
            (await db.HistoricalPriceSyncStates.SingleAsync()).Status);
        Assert.Empty(await db.HistoricalAdjustedPrices.ToListAsync());
    }

    /// <summary>驗證市場辨識成功後會移除同一代號過期的 Unknown 同步狀態。</summary>
    [Fact]
    public async Task SyncAsync_ClearsUnknownStateAfterMarketDetectionSucceeds()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("重新辨識", "6666", StockMarket.Unknown, null));
        db.HistoricalPriceSyncStates.Add(new HistoricalPriceSyncState
        {
            Market = StockMarket.Unknown,
            Symbol = "6666",
            Status = HistoricalPriceSyncStatus.NoData,
            SafeMessage = "先前沒有資料",
            LastAttemptedAtUtc = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) =>
            market == StockMarket.Twse
                ? Success(symbol, "6666.TW", 10m)
                : throw new HistoricalPriceProviderException("no_data", "沒有可用行情"));

        await new HistoricalMarketDataSynchronizer(db, provider)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(StockMarket.Twse, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        Assert.DoesNotContain(await db.HistoricalPriceSyncStates.ToListAsync(), state => state.Market == StockMarket.Unknown);
        Assert.Equal(HistoricalPriceSyncStatus.Success,
            (await db.HistoricalPriceSyncStates.SingleAsync()).Status);
    }

    /// <summary>驗證單一標的失敗會保留成功資料並繼續處理其他標的。</summary>
    [Fact]
    public async Task SyncAsync_IsolatesOneInstrumentFailure()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("失敗標的", "1111", StockMarket.Twse, null),
            CreateStock("成功標的", "2222", StockMarket.Twse, null));
        db.HistoricalAdjustedPrices.Add(new HistoricalAdjustedPrice
        {
            Market = StockMarket.Twse,
            Symbol = "1111",
            TradingDate = new DateOnly(2026, 8, 6),
            AdjustedClose = 90m,
            Provider = "old",
            FetchedAtUtc = DateTime.UtcNow.AddDays(-2),
        });
        db.HistoricalPriceSyncStates.Add(new HistoricalPriceSyncState
        {
            Market = StockMarket.Twse,
            Symbol = "1111",
            LastAttemptedAtUtc = DateTime.UtcNow.AddDays(-2),
            LastSucceededAtUtc = DateTime.UtcNow.AddDays(-2),
            LatestTradingDate = new DateOnly(2026, 8, 6),
            Status = HistoricalPriceSyncStatus.Success,
        });
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, symbol, _, _) =>
        {
            if (symbol == "1111")
                throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
            return Success(symbol, "2222.TW", 120m);
        });

        await new HistoricalMarketDataSynchronizer(db, provider)
            .SyncAsync(new DateOnly(2026, 8, 7));

        var failedState = await db.HistoricalPriceSyncStates.SingleAsync(state => state.Symbol == "1111");
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError, failedState.Status);
        Assert.NotNull(failedState.LastSucceededAtUtc);
        Assert.Equal(90m, (await db.HistoricalAdjustedPrices.SingleAsync(price => price.Symbol == "1111")).AdjustedClose);
        Assert.Equal(HistoricalPriceSyncStatus.Success,
            (await db.HistoricalPriceSyncStates.SingleAsync(state => state.Symbol == "2222")).Status);
    }

    /// <summary>建立具有固定欄位的測試持股。</summary>
    private static Stock CreateStock(string name, string symbol, StockMarket market, string? broker)
        => new()
        {
            Name = name,
            Symbol = symbol,
            Market = market,
            InstrumentType = StockInstrumentType.Stock,
            Shares = 10m,
            BuyPrice = 10m,
            CurrentPrice = 11m,
            Broker = broker,
        };

    /// <summary>建立 fake provider 的成功回應，使用固定交易日供 upsert 驗證。</summary>
    private static HistoricalPriceProviderResult Success(string symbol, string resolvedSymbol, decimal price)
        => new(
            "YahooChart",
            resolvedSymbol,
            "TAI",
            "TWD",
            [new HistoricalPricePoint(new DateOnly(2026, 8, 6), price)]);

    /// <summary>建立使用既有 SQLite connection 的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>開啟供多個 EF context 共用的 in-memory SQLite connection。</summary>
    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>提供可記錄請求且由測試決定結果的歷史行情 provider。</summary>
    private sealed class FakeProvider : IHistoricalAdjustedPriceProvider
    {
        private readonly Func<StockMarket, string, DateOnly, DateOnly, HistoricalPriceProviderResult> _handler;

        /// <summary>初始化 fake provider 行為。</summary>
        public FakeProvider(Func<StockMarket, string, DateOnly, DateOnly, HistoricalPriceProviderResult> handler)
        {
            _handler = handler;
        }

        /// <summary>記錄每次 provider 請求的市場、代號與日期範圍。</summary>
        public List<ProviderRequest> Requests { get; } = [];

        /// <summary>執行測試指定的 provider 回應。</summary>
        public Task<HistoricalPriceProviderResult> GetPricesAsync(
            StockMarket market,
            string symbol,
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new ProviderRequest(market, symbol, startDate, endDate));
            return Task.FromResult(_handler(market, symbol, startDate, endDate));
        }
    }

    /// <summary>保存 fake provider 的單次請求參數。</summary>
    private sealed record ProviderRequest(
        StockMarket Market,
        string Symbol,
        DateOnly StartDate,
        DateOnly EndDate);
}
