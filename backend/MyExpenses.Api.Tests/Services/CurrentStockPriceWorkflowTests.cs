using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class CurrentStockPriceWorkflowTests
{
    /// <summary>驗證目前價格 workflow 依市場分流並將 Unknown 目標保留為安全失敗。</summary>
    [Fact]
    public async Task RunAsync_UpdatesTwseAndTpexTargetsWithoutGuessingUnknownMarket()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("2330", StockMarket.Twse),
            CreateStock("6488", StockMarket.Tpex),
            CreateStock("9999", StockMarket.Unknown));
        await db.SaveChangesAsync();
        var twse = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]));
        var tpex = new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ =>
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)]));
        var workflow = new CurrentStockPriceWorkflow(db, twse, tpex);

        var result = await workflow.RunAsync();

        Assert.Equal(3, result.TargetCount);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal("IncompleteTargets", result.ResultCode);
        Assert.Equal(100m, await db.Stocks.Where(stock => stock.Symbol == "2330").Select(stock => stock.CurrentPrice).SingleAsync());
        Assert.Equal(88m, await db.Stocks.Where(stock => stock.Symbol == "6488").Select(stock => stock.CurrentPrice).SingleAsync());
        Assert.DoesNotContain("9999", twse.Requests);
        Assert.DoesNotContain("9999", tpex.Requests);
    }

    /// <summary>驗證 provider 回傳零匹配時不更新持股並分類為永久目標失敗。</summary>
    [Fact]
    public async Task RunAsync_ClassifiesZeroMatchingPriceAsPermanentFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("2330", StockMarket.Twse));
        await db.SaveChangesAsync();
        var twse = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("1101", 20m)]));
        var workflow = new CurrentStockPriceWorkflow(
            db,
            twse,
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync();

        Assert.Equal("NoMatchingPrice", result.ResultCode);
        Assert.Equal(ScheduledJobRetryClassification.Permanent, result.Retryability);
        Assert.Equal(0, result.AffectedCount);
        Assert.Equal(0m, await db.Stocks.Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證單一市場 transient provider failure 會保留可重試 typed 結果。</summary>
    [Fact]
    public async Task RunAsync_ReturnsRetryableProviderFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("2330", StockMarket.Twse));
        await db.SaveChangesAsync();
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
                CurrentPriceProviderResult.Failed("TWSE", "ProviderUnavailable", "暫時無法使用", true)),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync();

        Assert.Equal(ScheduledJobRetryClassification.Retryable, result.Retryability);
        Assert.Equal("ProviderUnavailable", result.ResultCode);
        Assert.Equal(1, result.FailedCount);
    }

    /// <summary>驗證重試時目前價格 workflow 只處理 execution 已凍結的持股目標。</summary>
    [Fact]
    public async Task RunAsync_UsesFrozenTargetKeys()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("2330", StockMarket.Twse),
            CreateStock("1101", StockMarket.Twse));
        await db.SaveChangesAsync();
        var twse = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
            CurrentPriceProviderResult.Success(
                "TWSE",
                [new CurrentPriceRecord("2330", 100m), new CurrentPriceRecord("1101", 20m)]));
        var workflow = new CurrentStockPriceWorkflow(
            db,
            twse,
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync(frozenTargetKeys: ["1"]);

        Assert.Equal(1, result.TargetCount);
        Assert.Equal(["1"], result.TargetKeys);
        Assert.Equal(100m, await db.Stocks.Where(stock => stock.Symbol == "2330").Select(stock => stock.CurrentPrice).SingleAsync());
        Assert.Equal(0m, await db.Stocks.Where(stock => stock.Symbol == "1101").Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證目前價格 workflow 回傳 provider、updated、unmatched、invalid 與 failed counts。</summary>
    [Fact]
    public async Task RunAsync_ReturnsProviderAndUpdateCounts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("2330", StockMarket.Twse));
        await db.SaveChangesAsync();
        var twse = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
            CurrentPriceProviderResult.Success(
                "TWSE",
                [
                    new CurrentPriceRecord("2330", 100m),
                    new CurrentPriceRecord("1101", 20m),
                    new CurrentPriceRecord("9999", null),
                ]));
        var workflow = new CurrentStockPriceWorkflow(
            db,
            twse,
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync();

        Assert.Equal(3, result.ProviderRecordCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(2, result.UnmatchedCount);
        Assert.Equal(1, result.InvalidCount);
        Assert.Equal(0, result.FailedCount);
    }

    /// <summary>驗證 provider 回應期間持股 identity 改變時不會把價格寫入新標的。</summary>
    [Fact]
    public async Task RunAsync_RejectsTargetIdentityChangedDuringProviderRequest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("2330", StockMarket.Twse));
        await db.SaveChangesAsync();
        var twse = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
        {
            var stock = db.Stocks.Single();
            stock.Symbol = "6488";
            stock.Market = StockMarket.Tpex;
            db.SaveChanges();
            return CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]);
        });
        var workflow = new CurrentStockPriceWorkflow(
            db,
            twse,
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync();

        Assert.Equal("TargetChanged", result.ResultCode);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0m, await db.Stocks.Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>建立固定持股測試資料。</summary>
    private static Stock CreateStock(string symbol, StockMarket market)
        => new()
        {
            Name = symbol,
            Symbol = symbol,
            Market = market,
            InstrumentType = StockInstrumentType.Stock,
            Shares = 1m,
            BuyPrice = 10m,
            CurrentPrice = 0m,
        };

    /// <summary>建立使用已開啟 SQLite 連線的資料庫 context。</summary>
    private static AppDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>提供可記錄市場請求且由測試決定結果的 current-price provider。</summary>
    private sealed class FakeCurrentPriceProvider : ICurrentPriceProvider
    {
        private readonly Func<string, CurrentPriceProviderResult> _handler;

        /// <summary>初始化 fake provider 行為。</summary>
        public FakeCurrentPriceProvider(
            StockMarket market,
            string providerName,
            Func<string, CurrentPriceProviderResult> handler)
        {
            Market = market;
            ProviderName = providerName;
            _handler = handler;
        }

        /// <summary>取得 provider 安全名稱。</summary>
        public string ProviderName { get; }

        /// <summary>取得 provider 市場。</summary>
        public StockMarket Market { get; }

        /// <summary>保存 workflow 實際請求的正規化代號。</summary>
        public List<string> Requests { get; } = [];

        /// <summary>執行測試指定的 provider response。</summary>
        public Task<CurrentPriceProviderResult> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_handler(string.Empty));

        /// <summary>依 workflow 的市場請求記錄代號並回傳預設結果。</summary>
        public Task<CurrentPriceProviderResult> FetchAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            Requests.AddRange(symbols);
            return Task.FromResult(_handler(symbols.FirstOrDefault() ?? string.Empty));
        }
    }
}
