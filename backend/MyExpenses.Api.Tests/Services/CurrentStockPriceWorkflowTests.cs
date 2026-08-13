using System.Globalization;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>驗證已知市場 provider failure 回應後會重新檢查 frozen identity。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_RevalidatesKnownTargetAfterProviderFailure(bool deleteTarget)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("2330", StockMarket.Twse);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        var twse = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
        {
            var current = db.Stocks.Single(item => item.Id == stockId);
            if (deleteTarget)
                db.Stocks.Remove(current);
            else
                current.Symbol = "1101";
            db.SaveChanges();
            return CurrentPriceProviderResult.Failed("TWSE", "ProviderUnavailable", "暫時無法使用", true);
        });
        var workflow = new CurrentStockPriceWorkflow(
            db,
            twse,
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync();

        var targetKey = stockId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal([targetKey], result.TargetKeys);
        Assert.Equal(1, result.TargetCount);
        Assert.Equal("TargetChanged", result.FailedTargetCodes[targetKey]);
        Assert.Equal("TargetChanged", result.ResultCode);
        Assert.Equal(ScheduledJobRetryClassification.Permanent, result.Retryability);
        Assert.Equal(0, result.AffectedCount);
        if (!deleteTarget)
            Assert.Equal(0m, await db.Stocks.Where(item => item.Id == stockId).Select(item => item.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證 known provider 成功但缺價或無效價時仍會逐 ID 重新檢查 frozen identity。</summary>
    [Theory]
    [InlineData("missing", "delete")]
    [InlineData("missing", "symbol")]
    [InlineData("missing", "market")]
    [InlineData("invalid", "delete")]
    [InlineData("invalid", "symbol")]
    [InlineData("invalid", "market")]
    public async Task RunAsync_RevalidatesKnownTargetsBeforeApplyingPriceDataFailure(
        string priceFailure,
        string mutation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var changed = CreateStock("第一筆", "2330", StockMarket.Twse);
        var survivor = CreateStock("第二筆", "2330", StockMarket.Twse);
        db.Stocks.AddRange(changed, survivor);
        await db.SaveChangesAsync();
        var changedId = changed.Id;
        var survivorId = survivor.Id;
        var provider = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
        {
            var current = db.Stocks.Single(item => item.Id == changedId);
            if (mutation == "delete")
                db.Stocks.Remove(current);
            else if (mutation == "symbol")
                current.Symbol = "1101";
            else
                current.Market = StockMarket.Tpex;
            db.SaveChanges();
            return priceFailure == "missing"
                ? CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("1101", 20m)])
                : CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", null)]);
        });
        var workflow = new CurrentStockPriceWorkflow(
            db,
            provider,
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync();

        var changedKey = changedId.ToString(CultureInfo.InvariantCulture);
        var survivorKey = survivorId.ToString(CultureInfo.InvariantCulture);
        var expectedDataCode = priceFailure == "missing" ? "NoMatchingPrice" : "InvalidPrice";
        Assert.Equal(2, result.TargetCount);
        Assert.Equal([changedKey, survivorKey], result.TargetKeys);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Equal("TargetChanged", result.FailedTargetCodes[changedKey]);
        Assert.Equal(expectedDataCode, result.FailedTargetCodes[survivorKey]);
        Assert.Equal("MultipleFailures", result.ResultCode);
        Assert.Equal(0, result.AffectedCount);
        Assert.Equal(0m,
            await db.Stocks.Where(item => item.Id == survivorId).Select(item => item.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證 Unknown catalog failure 回應後會重新檢查 frozen identity。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_RevalidatesUnknownTargetAfterCatalogFailure(bool deleteTarget)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("2330", StockMarket.Unknown);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        var catalog = new FakeMarketCatalogService(
            new OfficialMarketCatalogSnapshot(
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
                CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true)),
            () =>
            {
                var current = db.Stocks.Single(item => item.Id == stockId);
                if (deleteTarget)
                    db.Stocks.Remove(current);
                else
                    current.Market = StockMarket.Tpex;
                db.SaveChanges();
            });
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")),
            catalogService: catalog);

        var result = await workflow.RunAsync();

        var targetKey = stockId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal([targetKey], result.TargetKeys);
        Assert.Equal(1, result.TargetCount);
        Assert.Equal("TargetChanged", result.FailedTargetCodes[targetKey]);
        Assert.Equal("TargetChanged", result.ResultCode);
        Assert.Equal(ScheduledJobRetryClassification.Permanent, result.Retryability);
        Assert.Equal(0, result.AffectedCount);
        if (!deleteTarget)
        {
            var stored = await db.Stocks.SingleAsync(item => item.Id == stockId);
            Assert.Equal(StockMarket.Tpex, stored.Market);
            Assert.Equal(0m, stored.CurrentPrice);
        }
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

    /// <summary>驗證跨 attempt 的 frozen Stock ID 不會漂移到已改變或刪除的新身分。</summary>
    [Theory]
    [InlineData("symbol")]
    [InlineData("market")]
    [InlineData("delete")]
    public async Task RunAsync_PreservesOriginalFrozenIdentityAcrossAttempts(string mutation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("2330", StockMarket.Twse);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        var twseCalls = 0;
        var tpexCalls = 0;
        var twse = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
        {
            twseCalls++;
            return twseCalls == 1
                ? CurrentPriceProviderResult.Failed("TWSE", "ProviderUnavailable", "暫時無法使用", true)
                : CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("1101", 20m)]);
        });
        var tpex = new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ =>
        {
            tpexCalls++;
            return CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("2330", 88m)]);
        });
        var workflow = new CurrentStockPriceWorkflow(db, twse, tpex);

        var first = await workflow.RunAsync();
        var current = db.Stocks.Single(item => item.Id == stockId);
        if (mutation == "symbol")
            current.Symbol = "1101";
        else if (mutation == "market")
            current.Market = StockMarket.Tpex;
        else
            db.Stocks.Remove(current);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var second = await workflow.RunAsync(frozenTargetKeys: first.TargetKeys);

        var targetKey = stockId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal([targetKey], second.TargetKeys);
        Assert.Equal(1, second.TargetCount);
        Assert.Equal(0, second.SucceededCount);
        Assert.Equal(1, second.FailedCount);
        Assert.Equal("TargetChanged", second.FailedTargetCodes[targetKey]);
        Assert.Equal("TargetChanged", second.ResultCode);
        Assert.Equal(1, twseCalls);
        Assert.Equal(0, tpexCalls);
        if (mutation != "delete")
            Assert.Equal(0m, await db.Stocks.Where(item => item.Id == stockId).Select(item => item.CurrentPrice).SingleAsync());
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

    /// <summary>驗證已知市場 provider 回傳空 catalog 時會分類為可重試服務失敗。</summary>
    [Fact]
    public async Task RunAsync_ClassifiesEmptyKnownMarketCatalogAsRetryableProviderFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("2330", StockMarket.Twse));
        await db.SaveChangesAsync();
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync();

        Assert.Equal("ProviderUnavailable", result.ResultCode);
        Assert.Equal(ScheduledJobRetryClassification.Retryable, result.Retryability);
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

    /// <summary>驗證未知市場唯一命中時同一次 execution 會更新同代號持股的市場與價格。</summary>
    [Fact]
    public async Task RunAsync_ResolvesUnknownMarketAndUpdatesDuplicateHoldings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(CreateStock("第一筆", "2330", StockMarket.Unknown), CreateStock("第二筆", " 2330 ", StockMarket.Unknown));
        await db.SaveChangesAsync();
        var catalog = new FakeMarketCatalogService(
            new OfficialMarketCatalogSnapshot(
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m, "台積電")]),
                CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m, "環球晶")]))) ;
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")),
            catalogService: catalog);

        var result = await workflow.RunAsync();

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal("Completed", result.ResultCode);
        Assert.Equal(2, result.ProviderRecordCount);
        Assert.Equal(1, result.UnmatchedCount);
        Assert.Equal(0, result.InvalidCount);
        Assert.All(await db.Stocks.ToListAsync(), stock =>
        {
            Assert.Equal(StockMarket.Twse, stock.Market);
            Assert.Equal(100m, stock.CurrentPrice);
        });
        Assert.Equal(1, catalog.FetchCount);
    }

    /// <summary>驗證未知市場雙邊命中、找不到、來源失敗及空白代號不會被猜測。</summary>
    [Fact]
    public async Task RunAsync_KeepsUnknownWhenMarketDetectionIsNotDefinitive()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("雙邊", "1111", StockMarket.Unknown),
            CreateStock("找不到", "2222", StockMarket.Unknown),
            CreateStock("空白", "   ", StockMarket.Unknown));
        await db.SaveChangesAsync();
        var catalog = new FakeMarketCatalogService(
            new OfficialMarketCatalogSnapshot(
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("1111", 10m), new CurrentPriceRecord("3333", 30m)]),
                CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("1111", 11m), new CurrentPriceRecord("4444", 40m)])));
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")),
            catalogService: catalog);

        var result = await workflow.RunAsync();

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(3, result.FailedCount);
        Assert.Equal(StockMarket.Unknown, await db.Stocks.Where(stock => stock.Symbol == "1111").Select(stock => stock.Market).SingleAsync());
        Assert.Equal(StockMarket.Unknown, await db.Stocks.Where(stock => stock.Symbol == "2222").Select(stock => stock.Market).SingleAsync());
        Assert.Equal("MultipleFailures", result.ResultCode);
        Assert.Equal(1, catalog.FetchCount);
    }

    /// <summary>驗證官方 catalog 單邊失敗時已知市場仍可更新，未知市場則保持待辨識。</summary>
    [Fact]
    public async Task RunAsync_UpdatesKnownMarketWhenUnknownMarketCatalogIsIncomplete()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("已知上市", "2330", StockMarket.Twse),
            CreateStock("未知標的", "6488", StockMarket.Unknown));
        await db.SaveChangesAsync();
        var catalog = new FakeMarketCatalogService(
            new OfficialMarketCatalogSnapshot(
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
                CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true)));
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")),
            catalogService: catalog);

        var result = await workflow.RunAsync();

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(100m, await db.Stocks.Where(stock => stock.Symbol == "2330").Select(stock => stock.CurrentPrice).SingleAsync());
        Assert.Equal(StockMarket.Unknown, await db.Stocks.Where(stock => stock.Symbol == "6488").Select(stock => stock.Market).SingleAsync());
        Assert.Equal("IncompleteTargets", result.ResultCode);
        Assert.Equal(ScheduledJobRetryClassification.Retryable, result.Retryability);
    }

    /// <summary>驗證市場辨識期間使用者改選市場時不會覆寫新身分。</summary>
    [Fact]
    public async Task RunAsync_RejectsUnknownTargetChangedDuringMarketDetection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("變更市場", "2330", StockMarket.Unknown));
        await db.SaveChangesAsync();
        var catalog = new FakeMarketCatalogService(
            new OfficialMarketCatalogSnapshot(
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
                CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)])),
            () =>
            {
                var stock = db.Stocks.Single();
                stock.Market = StockMarket.Tpex;
                db.SaveChanges();
            });
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")),
            catalogService: catalog);

        var result = await workflow.RunAsync();

        Assert.Equal("TargetChanged", result.ResultCode);
        Assert.Equal(StockMarket.Tpex, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        Assert.Equal(0m, await db.Stocks.Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證同一已知市場 target 只拒絕 provider 回應期間離開的持股。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_UpdatesSurvivingKnownHoldingWhenSiblingLeavesTarget(bool deleteChangedHolding)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var changed = CreateStock("第一筆", "2330", StockMarket.Twse);
        var survivor = CreateStock("第二筆", "2330", StockMarket.Twse);
        db.Stocks.AddRange(changed, survivor);
        await db.SaveChangesAsync();
        var changedId = changed.Id;
        var survivorId = survivor.Id;
        var twse = new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
        {
            var current = db.Stocks.Single(stock => stock.Id == changedId);
            if (deleteChangedHolding)
                db.Stocks.Remove(current);
            else
                current.Symbol = "1101";
            db.SaveChanges();
            return CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]);
        });
        var workflow = new CurrentStockPriceWorkflow(
            db,
            twse,
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var result = await workflow.RunAsync();

        Assert.Equal(2, result.TargetCount);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal("IncompleteTargets", result.ResultCode);
        Assert.Contains(survivorId.ToString(CultureInfo.InvariantCulture), result.SucceededTargetKeys);
        Assert.Equal("TargetChanged", result.FailedTargetCodes[changedId.ToString(CultureInfo.InvariantCulture)]);
        Assert.Equal(100m,
            await db.Stocks.Where(stock => stock.Id == survivorId).Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證未知市場同代號只更新 catalog 回應期間仍符合身分的持股。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_ResolvesSurvivingUnknownHoldingWhenSiblingLeavesTarget(
        bool deleteChangedHolding,
        bool validPrice)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var changed = CreateStock("第一筆", "2330", StockMarket.Unknown);
        var survivor = CreateStock("第二筆", "2330", StockMarket.Unknown);
        db.Stocks.AddRange(changed, survivor);
        await db.SaveChangesAsync();
        var changedId = changed.Id;
        var survivorId = survivor.Id;
        var catalog = new FakeMarketCatalogService(
            new OfficialMarketCatalogSnapshot(
                CurrentPriceProviderResult.Success(
                    "TWSE",
                    [new CurrentPriceRecord("2330", validPrice ? 100m : null)]),
                CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)])),
            () =>
            {
                var current = db.Stocks.Single(stock => stock.Id == changedId);
                if (deleteChangedHolding)
                    db.Stocks.Remove(current);
                else
                    current.Symbol = "1101";
                db.SaveChanges();
            });
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")),
            catalogService: catalog);

        var result = await workflow.RunAsync();

        Assert.Equal(2, result.TargetCount);
        Assert.Equal(validPrice ? 1 : 0, result.SucceededCount);
        Assert.Equal(validPrice ? 1 : 2, result.FailedCount);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(validPrice ? "IncompleteTargets" : "MultipleFailures", result.ResultCode);
        Assert.Equal("TargetChanged", result.FailedTargetCodes[changedId.ToString(CultureInfo.InvariantCulture)]);
        if (validPrice)
            Assert.Contains(survivorId.ToString(CultureInfo.InvariantCulture), result.SucceededTargetKeys);
        else
            Assert.Equal("InvalidPrice", result.FailedTargetCodes[survivorId.ToString(CultureInfo.InvariantCulture)]);
        var storedSurvivor = await db.Stocks.SingleAsync(stock => stock.Id == survivorId);
        Assert.Equal(StockMarket.Twse, storedSurvivor.Market);
        Assert.Equal(validPrice ? 100m : 0m, storedSurvivor.CurrentPrice);
    }

    /// <summary>驗證 host 取消中止第二個市場時保留第一個市場已提交的目前價格進度。</summary>
    [Fact]
    public async Task RunAsync_ThrowsPartialResultWhenHostCancellationStopsSecondMarket()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var twseStock = CreateStock("1111", StockMarket.Twse);
        var tpexStock = CreateStock("2222", StockMarket.Tpex);
        db.Stocks.AddRange(twseStock, tpexStock);
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("1111", 100m)])),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }));

        var exception = await Record.ExceptionAsync(() => workflow.RunAsync(cancellation.Token));

        Assert.NotNull(exception);
        Assert.Equal("CurrentStockPricePartialFailureException", exception.GetType().Name);
        var partialProperty = exception.GetType().GetProperty("PartialResult");
        Assert.NotNull(partialProperty);
        var partial = Assert.IsType<ScheduledJobWorkflowResult>(partialProperty.GetValue(exception));
        Assert.Equal(2, partial.TargetCount);
        Assert.Equal([twseStock.Id.ToString(CultureInfo.InvariantCulture)], partial.SucceededTargetKeys);
        Assert.Equal(tpexStock.Id.ToString(CultureInfo.InvariantCulture), Assert.Single(partial.FailedTargetCodes).Key);
        Assert.Equal("Canceled", partial.FailedTargetCodes[tpexStock.Id.ToString(CultureInfo.InvariantCulture)]);
        Assert.Equal([twseStock.Id.ToString(CultureInfo.InvariantCulture)], partial.AffectedRowKeys);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal(100m, await db.Stocks.Where(stock => stock.Id == twseStock.Id)
            .Select(stock => stock.CurrentPrice).SingleAsync());
    }

    /// <summary>驗證列舉目標後官方 catalog raw SQLite busy 會轉為完整 partial result。</summary>
    [Fact]
    public async Task RunAsync_ThrowsPartialResultWhenCatalogFetchRaisesDatabaseBusy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("2330", StockMarket.Unknown);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")),
            catalogService: new ThrowingMarketCatalogService(new SqliteException("database is locked", 5)));

        var exception = await Record.ExceptionAsync(() => workflow.RunAsync());

        Assert.NotNull(exception);
        Assert.Equal("CurrentStockPricePartialFailureException", exception.GetType().Name);
        var partialProperty = exception.GetType().GetProperty("PartialResult");
        Assert.NotNull(partialProperty);
        var partial = Assert.IsType<ScheduledJobWorkflowResult>(partialProperty.GetValue(exception));
        var targetKey = stock.Id.ToString(CultureInfo.InvariantCulture);
        Assert.Equal([targetKey], partial.TargetKeys);
        Assert.Equal("TransientFailure", partial.FailedTargetCodes[targetKey]);
        Assert.Equal(ScheduledJobRetryClassification.Retryable, partial.Retryability);
        Assert.IsType<SqliteException>(exception.InnerException);
    }

    /// <summary>驗證官方 catalog provider 的非 host OCE 讓 Unknown target 回傳可重試 typed failure。</summary>
    [Fact]
    public async Task RunAsync_ReturnsRetryableUnknownMarketFailureWhenCatalogProviderTimesOut()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("2330", StockMarket.Unknown);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var catalog = new OfficialMarketCatalogService(
            new ThrowingCurrentPriceProvider("TWSE", StockMarket.Twse, new OperationCanceledException("內部 timeout")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ =>
                CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)])));
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ => CurrentPriceProviderResult.NoWork("TWSE")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")),
            catalogService: catalog);

        var result = await workflow.RunAsync();

        var targetKey = stock.Id.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(ScheduledJobRetryClassification.Retryable, result.Retryability);
        Assert.Equal("MarketDetectionUnavailable", result.FailedTargetCodes[targetKey]);
    }

    /// <summary>驗證已列舉 known targets 後 provider raw 永久例外會轉為完整 partial result。</summary>
    [Fact]
    public async Task RunAsync_ThrowsPartialResultWhenKnownProviderRaisesPermanentException()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var first = CreateStock("1111", StockMarket.Twse);
        var second = CreateStock("2222", StockMarket.Tpex);
        db.Stocks.AddRange(first, second);
        await db.SaveChangesAsync();
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new ThrowingCurrentPriceProvider("TWSE", StockMarket.Twse, new InvalidOperationException("raw provider failure")),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var exception = await Record.ExceptionAsync(() => workflow.RunAsync());

        Assert.NotNull(exception);
        Assert.Equal("CurrentStockPricePartialFailureException", exception.GetType().Name);
        var partialProperty = exception.GetType().GetProperty("PartialResult");
        Assert.NotNull(partialProperty);
        var partial = Assert.IsType<ScheduledJobWorkflowResult>(partialProperty.GetValue(exception));
        Assert.Equal(2, partial.TargetCount);
        Assert.Equal(2, partial.FailedCount);
        Assert.All(partial.FailedTargetCodes.Values, code => Assert.Equal("DatabaseFailure", code));
        Assert.Equal(ScheduledJobRetryClassification.Permanent, partial.Retryability);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    /// <summary>驗證 provider failure 後 revalidation query SQLite busy 會轉為完整 retryable partial result。</summary>
    [Fact]
    public async Task RunAsync_ThrowsPartialResultWhenKnownTargetRevalidationRaisesDatabaseBusy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new RevalidationQueryFailureInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        var first = CreateStock("1111", StockMarket.Twse);
        var second = CreateStock("2222", StockMarket.Tpex);
        db.Stocks.AddRange(first, second);
        await db.SaveChangesAsync();
        var workflow = new CurrentStockPriceWorkflow(
            db,
            new FakeCurrentPriceProvider(StockMarket.Twse, "TWSE", _ =>
            {
                interceptor.Arm();
                return CurrentPriceProviderResult.Failed("TWSE", "ProviderUnavailable", "暫時無法使用", true);
            }),
            new FakeCurrentPriceProvider(StockMarket.Tpex, "TPEx", _ => CurrentPriceProviderResult.NoWork("TPEx")));

        var exception = await Record.ExceptionAsync(() => workflow.RunAsync());

        Assert.NotNull(exception);
        Assert.Equal("CurrentStockPricePartialFailureException", exception.GetType().Name);
        var partialProperty = exception.GetType().GetProperty("PartialResult");
        Assert.NotNull(partialProperty);
        var partial = Assert.IsType<ScheduledJobWorkflowResult>(partialProperty.GetValue(exception));
        Assert.Equal([first.Id.ToString(CultureInfo.InvariantCulture), second.Id.ToString(CultureInfo.InvariantCulture)], partial.TargetKeys);
        Assert.Equal(2, partial.FailedCount);
        Assert.All(partial.FailedTargetCodes.Values, code => Assert.Equal("TransientFailure", code));
        Assert.Equal(ScheduledJobRetryClassification.Retryable, partial.Retryability);
        var sqlite = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(5, sqlite.SqliteErrorCode);
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

    /// <summary>建立含名稱的固定持股測試資料。</summary>
    private static Stock CreateStock(string name, string symbol, StockMarket market)
        => new()
        {
            Name = name,
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

    /// <summary>提供會拋出 raw 例外的目前價格 provider。</summary>
    private sealed class ThrowingCurrentPriceProvider : ICurrentPriceProvider
    {
        private readonly Exception _exception;

        /// <summary>初始化 provider 名稱、市場與測試例外。</summary>
        public ThrowingCurrentPriceProvider(string providerName, StockMarket market, Exception exception)
        {
            ProviderName = providerName;
            Market = market;
            _exception = exception;
        }

        /// <summary>取得 provider 安全名稱。</summary>
        public string ProviderName { get; }

        /// <summary>取得 provider 市場。</summary>
        public StockMarket Market { get; }

        /// <summary>拋出測試指定的 raw provider 例外。</summary>
        public Task<CurrentPriceProviderResult> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromException<CurrentPriceProviderResult>(_exception);
    }

    /// <summary>可在 provider failure 後精準讓下一個 Stocks revalidation query 拋 SQLite busy。</summary>
    private sealed class RevalidationQueryFailureInterceptor : DbCommandInterceptor
    {
        private int _armed;

        /// <summary>安排下一個 Stocks reader query 拋出 SQLite busy。</summary>
        public void Arm()
        {
            Interlocked.Exchange(ref _armed, 1);
        }

        /// <summary>在非同步 Stocks reader query 執行前注入一次 SQLite busy。</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("Stocks", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
                throw new SqliteException("database is locked", 5);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>提供可控制 snapshot 與呼叫次數的官方市場 catalog fake。</summary>
    private sealed class FakeMarketCatalogService : IOfficialMarketCatalogService
    {
        private readonly Action? _beforeFetch;

        /// <summary>初始化固定 snapshot 與可選的並行變更動作。</summary>
        public FakeMarketCatalogService(OfficialMarketCatalogSnapshot snapshot, Action? beforeFetch = null)
        {
            Snapshot = snapshot;
            _beforeFetch = beforeFetch;
        }

        /// <summary>取得測試固定的官方市場 snapshot。</summary>
        public OfficialMarketCatalogSnapshot Snapshot { get; }

        /// <summary>取得 snapshot 被取得的次數。</summary>
        public int FetchCount { get; private set; }

        /// <summary>回傳測試指定的官方市場 snapshot。</summary>
        public Task<OfficialMarketCatalogSnapshot> FetchAsync(CancellationToken cancellationToken = default)
        {
            FetchCount++;
            _beforeFetch?.Invoke();
            return Task.FromResult(Snapshot);
        }

        /// <summary>以純 resolver 回傳測試 lookup 結果。</summary>
        public Task<OfficialMarketResolution> LookupAsync(string? symbol, CancellationToken cancellationToken = default)
            => Task.FromResult(OfficialMarketCatalogResolver.Resolve(Snapshot, symbol));
    }

    /// <summary>提供會直接拋出 raw catalog failure 的測試服務。</summary>
    private sealed class ThrowingMarketCatalogService : IOfficialMarketCatalogService
    {
        private readonly Exception _exception;

        /// <summary>初始化要由 catalog fetch 拋出的例外。</summary>
        public ThrowingMarketCatalogService(Exception exception)
        {
            _exception = exception;
        }

        /// <summary>直接拋出測試指定的 catalog failure。</summary>
        public Task<OfficialMarketCatalogSnapshot> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromException<OfficialMarketCatalogSnapshot>(_exception);

        /// <summary>此測試不會執行單一代號 lookup。</summary>
        public Task<OfficialMarketResolution> LookupAsync(string? symbol, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("不應呼叫 catalog lookup");
    }
}
