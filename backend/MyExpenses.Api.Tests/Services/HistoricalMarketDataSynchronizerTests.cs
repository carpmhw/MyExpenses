using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        var synchronizer = new HistoricalMarketDataSynchronizer(
            db,
            provider,
            catalogService: new FakeCatalogService(CreateCatalog(
                [new CurrentPriceRecord("2330", 100m)],
                [new CurrentPriceRecord("00679B", 50m)])));

        var result = await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));

        var stocks = await db.Stocks.OrderBy(stock => stock.Id).ToListAsync();
        Assert.Equal(StockMarket.Twse, stocks[0].Market);
        Assert.Equal(StockMarket.Twse, stocks[1].Market);
        Assert.Equal(StockMarket.Tpex, stocks[2].Market);
        Assert.Equal(StockMarket.Unknown, stocks[3].Market);
        Assert.Equal(2, await db.HistoricalAdjustedPrices.CountAsync());
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(2, result.ProcessedInstrumentCount);
        Assert.Equal(2, result.SuccessfulInstrumentCount);
        Assert.Equal(0, result.FailedInstrumentCount);
        Assert.Equal(4, result.AffectedCount);
        Assert.Equal(2, result.TargetCount);
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
        var synchronizer = new HistoricalMarketDataSynchronizer(
            db,
            provider,
            catalogService: new FakeCatalogService(CreateCatalog(
                [new CurrentPriceRecord("2330", 100m)],
                [new CurrentPriceRecord("6488", 50m)])));

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

        await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(new OfficialMarketCatalogSnapshot(
                    CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
                    CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true))))
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
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("9999", 10m)],
            [new CurrentPriceRecord("9999", 11m)]));

        await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
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
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", null)],
            [new CurrentPriceRecord("6488", 50m)]));

        await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
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
        var provider = new FakeProvider((_, _, _, _) =>
            throw new InvalidOperationException("不應呼叫歷史 provider"));
        var catalog = new FakeCatalogService(new OfficialMarketCatalogSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("7777", 10m)]),
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true)));

        await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: catalog)
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

        await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog(
                    [new CurrentPriceRecord("6666", 10m)],
                    [new CurrentPriceRecord("6488", 50m)])))
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

        await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog([], [])))
            .SyncAsync(new DateOnly(2026, 8, 7));

        var failedState = await db.HistoricalPriceSyncStates.SingleAsync(state => state.Symbol == "1111");
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError, failedState.Status);
        Assert.NotNull(failedState.LastSucceededAtUtc);
        Assert.Equal(90m, (await db.HistoricalAdjustedPrices.SingleAsync(price => price.Symbol == "1111")).AdjustedClose);
        Assert.Equal(HistoricalPriceSyncStatus.Success,
            (await db.HistoricalPriceSyncStates.SingleAsync(state => state.Symbol == "2222")).Status);
    }

    /// <summary>驗證歷史 provider 的非預期永久例外會轉成不可重試且不洩漏原訊息的 bounded failure。</summary>
    [Fact]
    public async Task SyncAsync_ClassifiesUnexpectedPermanentProviderExceptionAsProviderFailure()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("永久失敗", "2330", StockMarket.Twse, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new InvalidOperationException("不得暴露的 provider 內部錯誤"));
        var synchronizer = new HistoricalMarketDataSynchronizer(
            db,
            provider,
            catalogService: new FakeCatalogService(CreateCatalog([], [])));

        var result = await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(1, result.FailedInstrumentCount);
        Assert.Equal(0, result.SuccessfulInstrumentCount);
        Assert.False(result.RetryableFailure);
        Assert.Equal("ProviderFailure", result.ResultCode);
        Assert.Equal("ProviderFailure", result.FailedTargetCodes!["Twse:2330"]);
        var state = await db.HistoricalPriceSyncStates.SingleAsync();
        Assert.Equal("歷史行情同步失敗", state.SafeMessage);
        Assert.DoesNotContain("不得暴露", state.SafeMessage, StringComparison.Ordinal);
    }

    /// <summary>驗證已知市場 provider 的非 host OCE 會映射為可重試歷史行情 timeout。</summary>
    [Fact]
    public async Task SyncAsync_ClassifiesKnownProviderNonHostCancellationAsRetryableTimeout()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("上市", "2330", StockMarket.Twse, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new OperationCanceledException("內部 timeout"));

        var result = await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog([], [])))
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(1, result.FailedInstrumentCount);
        Assert.True(result.RetryableFailure);
        Assert.Equal("ProviderUnavailable", result.FailedTargetCodes!["Twse:2330"]);
        Assert.Equal("ProviderUnavailable", result.ResultCode);
        var state = await db.HistoricalPriceSyncStates.SingleAsync();
        Assert.Equal(StockMarket.Twse, state.Market);
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError, state.Status);
        Assert.Equal("歷史行情服務逾時", state.SafeMessage);
        Assert.DoesNotContain("內部 timeout", state.SafeMessage, StringComparison.Ordinal);
    }

    /// <summary>驗證 Unknown 市場唯一辨識後 Yahoo provider 的非 host OCE 保留市場並回傳可重試失敗。</summary>
    [Fact]
    public async Task SyncAsync_ClassifiesUnknownProviderNonHostCancellationAsRetryableTimeout()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("待辨識", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new OperationCanceledException("內部 timeout"));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.True(result.RetryableFailure);
        Assert.Equal("ProviderUnavailable", result.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal("ProviderUnavailable", result.ResultCode);
        Assert.Equal(StockMarket.Twse, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        var state = await db.HistoricalPriceSyncStates.SingleAsync();
        Assert.Equal(StockMarket.Twse, state.Market);
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError, state.Status);
        Assert.Equal("歷史行情服務逾時", state.SafeMessage);
    }

    /// <summary>驗證 provider failure 後唯一 frozen member 離開 request identity 時改列 TargetChanged。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncAsync_RevalidatesUniqueKnownTargetAfterProviderFailure(bool deleteTarget)
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("上市", "2330", StockMarket.Twse, null);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        var provider = new FakeProvider((_, _, _, _) =>
        {
            var current = db.Stocks.Single(item => item.Id == stockId);
            if (deleteTarget)
                db.Stocks.Remove(current);
            else
                current.Market = StockMarket.Tpex;
            db.SaveChanges();
            throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
        });

        var result = await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog([], [])))
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("TargetChanged", result.FailedTargetCodes!["Twse:2330"]);
        Assert.Equal("TargetChanged", result.ResultCode);
        Assert.False(result.RetryableFailure);
        Assert.Empty(await db.HistoricalPriceSyncStates.ToListAsync());
        Assert.Empty(await db.HistoricalAdjustedPrices.ToListAsync());
    }

    /// <summary>驗證 provider failure 後仍有 frozen sibling 符合 request identity 時保留 provider failure。</summary>
    [Fact]
    public async Task SyncAsync_KeepsProviderFailureWhenKnownSiblingStillMatches()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var changed = CreateStock("改市場", "2330", StockMarket.Twse, null);
        var survivor = CreateStock("仍上市", "2330", StockMarket.Twse, null);
        db.Stocks.AddRange(changed, survivor);
        await db.SaveChangesAsync();
        var changedId = changed.Id;
        var provider = new FakeProvider((_, _, _, _) =>
        {
            var current = db.Stocks.Single(item => item.Id == changedId);
            current.Market = StockMarket.Tpex;
            db.SaveChanges();
            throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
        });

        var result = await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog([], [])))
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("ProviderUnavailable", result.FailedTargetCodes!["Twse:2330"]);
        Assert.True(result.RetryableFailure);
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError,
            (await db.HistoricalPriceSyncStates.SingleAsync()).Status);
    }

    /// <summary>驗證重試時歷史行情同步器只處理 execution 已凍結的唯一標的。</summary>
    [Fact]
    public async Task SyncAsync_UsesFrozenTargetKeys()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("第一標的", "2330", StockMarket.Twse, null),
            CreateStock("第二標的", "1101", StockMarket.Twse, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, symbol, _, _) => Success(symbol, symbol + ".TW", 100m));

        var result = await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog([], [])))
            .SyncAsync(
                new DateOnly(2026, 8, 7),
                frozenTargetKeys: ["Twse:2330"]);

        Assert.Equal(1, result.ProcessedInstrumentCount);
        Assert.Single(provider.Requests);
        Assert.Equal("2330", provider.Requests[0].Symbol);
        Assert.Equal(["Twse:2330"], result.TargetKeys);
    }

    /// <summary>驗證未知市場候選的永久 HTTP 4xx 不會被誤分類為可重試 failure。</summary>
    [Fact]
    public async Task SyncAsync_DoesNotRetryUnknownMarketAfterPermanentProviderRejection()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("永久拒絕", "7777", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new InvalidOperationException("不應呼叫歷史 provider"));
        var catalog = new FakeCatalogService(new OfficialMarketCatalogSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("7777", 10m)]),
            CurrentPriceProviderResult.Failed("TPEx", "ProviderRejected", "服務拒絕請求", false)));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.False(result.RetryableFailure);
        Assert.Equal("MarketDetectionUnavailable", result.FailedTargetCodes!["Unknown:7777"]);
        Assert.Equal(StockMarket.Unknown, await db.Stocks.Select(stock => stock.Market).SingleAsync());
    }

    /// <summary>驗證 fresh synchronizer 對不相符的 frozen Unknown key 會 fail closed 為 TargetChanged。</summary>
    [Fact]
    public async Task SyncAsync_DoesNotIncludeKnownMarketForFrozenUnknownTarget()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("市場已變更", "2330", StockMarket.Twse, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, symbol, _, _) => Success(symbol, symbol + ".TW", 100m));

        var result = await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog([], [])))
            .SyncAsync(
                new DateOnly(2026, 8, 7),
                frozenTargetKeys: ["Unknown:2330"]);

        Assert.Equal(1, result.ProcessedInstrumentCount);
        Assert.Equal(1, result.TargetCount);
        Assert.Equal(["Unknown:2330"], result.TargetKeys);
        Assert.Equal("TargetChanged", result.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal("TargetChanged", result.ResultCode);
        Assert.Empty(provider.Requests);
    }

    /// <summary>驗證同代號已有明確市場時不會再次對 Unknown 持股發出 provider 請求。</summary>
    [Fact]
    public async Task SyncAsync_UsesKnownMarketOnceForMixedUnknownHoldings()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("明確市場", "2330", StockMarket.Twse, null),
            CreateStock("待辨識市場", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) => market == StockMarket.Twse
            ? Success(symbol, "2330.TW", 100m)
            : throw new HistoricalPriceProviderException("no_data", "沒有可用行情"));

        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));
        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(2, result.ProcessedInstrumentCount);
        Assert.Equal(2, result.SuccessfulInstrumentCount);
        Assert.Single(provider.Requests);
        Assert.All(await db.Stocks.ToListAsync(), stock => Assert.Equal(StockMarket.Twse, stock.Market));
    }

    /// <summary>驗證官方市場唯一命中後只對歷史 provider 發出一次正確 suffix 請求。</summary>
    [Fact]
    public async Task SyncAsync_UsesOfficialCatalogAndOneHistoricalRequestForUnknownSymbol()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("第一筆", "2330", StockMarket.Unknown, null),
            CreateStock("第二筆", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) =>
            market == StockMarket.Twse && symbol == "2330"
                ? Success(symbol, "2330.TW", 100m)
                : throw new InvalidOperationException("不應呼叫其他歷史市場"));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m, "台積電")],
            [new CurrentPriceRecord("6488", 88m, "環球晶")]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(StockMarket.Twse, await db.Stocks.Select(stock => stock.Market).FirstAsync());
        Assert.Single(provider.Requests);
        Assert.Equal(StockMarket.Twse, provider.Requests[0].Market);
        Assert.Equal("2330", provider.Requests[0].Symbol);
        Assert.Equal(1, catalog.FetchCount);
        Assert.Equal(1, result.SuccessfulInstrumentCount);
    }

    /// <summary>驗證多個未知代號共用一次官方 catalog 並各自只同步唯一歷史市場。</summary>
    [Fact]
    public async Task SyncAsync_UsesOneOfficialCatalogForMultipleUnknownSymbols()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("上市標的", "2330", StockMarket.Unknown, null),
            CreateStock("上櫃標的", "6488", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) =>
            (market, symbol) switch
            {
                (StockMarket.Twse, "2330") => Success(symbol, "2330.TW", 100m),
                (StockMarket.Tpex, "6488") => Success(symbol, "6488.TWO", 88m),
                _ => throw new InvalidOperationException("不應呼叫其他歷史市場"),
            });
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(2, result.SuccessfulInstrumentCount);
        Assert.Equal(1, catalog.FetchCount);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(StockMarket.Twse,
            await db.Stocks.Where(stock => stock.Symbol == "2330").Select(stock => stock.Market).SingleAsync());
        Assert.Equal(StockMarket.Tpex,
            await db.Stocks.Where(stock => stock.Symbol == "6488").Select(stock => stock.Market).SingleAsync());
    }

    /// <summary>驗證官方市場歧義時保留 Unknown 且不呼叫 Yahoo 歷史 provider。</summary>
    [Fact]
    public async Task SyncAsync_DoesNotFetchHistoryForAmbiguousOfficialMarket()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("雙邊代號", "9999", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) => throw new InvalidOperationException("不應呼叫"));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("9999", 10m)],
            [new CurrentPriceRecord("9999", 11m)]));

        await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Empty(provider.Requests);
        Assert.Equal(StockMarket.Unknown, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        Assert.Equal(HistoricalPriceSyncStatus.AmbiguousMarket,
            (await db.HistoricalPriceSyncStates.SingleAsync(state => state.Market == StockMarket.Unknown)).Status);
    }

    /// <summary>驗證官方市場來源失敗時保留 Unknown、不呼叫 Yahoo 且回傳可重試結果。</summary>
    [Fact]
    public async Task SyncAsync_DoesNotFetchHistoryWhenOfficialCatalogIsUnavailable()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("來源失敗", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) => throw new InvalidOperationException("不應呼叫"));
        var catalog = new FakeCatalogService(new OfficialMarketCatalogSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true)));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Empty(provider.Requests);
        Assert.True(result.RetryableFailure);
        Assert.Equal(StockMarket.Unknown, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError,
            (await db.HistoricalPriceSyncStates.SingleAsync()).Status);
    }

    /// <summary>驗證 catalog 回應後 Unknown frozen member 已離開時不保存過期辨識失敗狀態。</summary>
    [Theory]
    [InlineData("unavailable", "delete")]
    [InlineData("ambiguous", "symbol")]
    [InlineData("not-found", "market")]
    public async Task SyncAsync_RevalidatesUnknownTargetBeforePersistingCatalogFailure(
        string outcome,
        string mutation)
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("辨識期間離開", "2330", StockMarket.Unknown, null);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        var snapshot = outcome switch
        {
            "unavailable" => new OfficialMarketCatalogSnapshot(
                CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
                CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true)),
            "ambiguous" => CreateCatalog(
                [new CurrentPriceRecord("2330", 100m)],
                [new CurrentPriceRecord("2330", 88m)]),
            _ => CreateCatalog(
                [new CurrentPriceRecord("1101", 20m)],
                [new CurrentPriceRecord("6488", 88m)]),
        };
        var catalog = new FakeCatalogService(snapshot, () =>
        {
            var current = db.Stocks.Single(item => item.Id == stockId);
            if (mutation == "delete")
                db.Stocks.Remove(current);
            else if (mutation == "symbol")
                current.Symbol = "1101";
            else
                current.Market = StockMarket.Tpex;
            db.SaveChanges();
        });
        var provider = new FakeProvider((_, _, _, _) =>
            throw new InvalidOperationException("catalog failure 不應呼叫 Yahoo"));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("TargetChanged", result.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal("TargetChanged", result.ResultCode);
        Assert.False(result.RetryableFailure);
        Assert.Empty(await db.HistoricalPriceSyncStates.ToListAsync());
        Assert.Empty(provider.Requests);
    }

    /// <summary>驗證所有市場已知時歷史同步不會額外取得官方市場 catalog。</summary>
    [Fact]
    public async Task SyncAsync_DoesNotFetchOfficialCatalogWhenAllMarketsAreKnown()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("已知上市", "2330", StockMarket.Twse, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, symbol, _, _) => Success(symbol, symbol + ".TW", 100m));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 50m)]));

        await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(0, catalog.FetchCount);
        Assert.Single(provider.Requests);
    }

    /// <summary>驗證已知與未知同代號時仍依官方雙市場結果判定未知持股。</summary>
    [Fact]
    public async Task SyncAsync_KeepsMixedUnknownHoldingWhenOfficialMarketIsAmbiguous()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("已知市場", "2330", StockMarket.Twse, null),
            CreateStock("待辨識", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) =>
            market == StockMarket.Twse
                ? Success(symbol, "2330.TW", 100m)
                : throw new InvalidOperationException("不應呼叫上櫃歷史 provider"));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("2330", 101m)]));

        await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        var stocks = await db.Stocks.OrderBy(stock => stock.Id).ToListAsync();
        Assert.Equal(StockMarket.Twse, stocks[0].Market);
        Assert.Equal(StockMarket.Unknown, stocks[1].Market);
        Assert.Equal(1, catalog.FetchCount);
        Assert.Single(provider.Requests);
        Assert.Equal(HistoricalPriceSyncStatus.AmbiguousMarket,
            (await db.HistoricalPriceSyncStates.SingleAsync(state => state.Market == StockMarket.Unknown)).Status);
    }

    /// <summary>驗證混合已知與未知同代號時，未知 target 的失敗不會被已知 target key 吞掉。</summary>
    [Fact]
    public async Task SyncAsync_PreservesMixedTargetFailureDisposition()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("已知市場", "2330", StockMarket.Twse, null),
            CreateStock("待辨識", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) =>
            market == StockMarket.Twse
                ? Success(symbol, "2330.TW", 100m)
                : throw new InvalidOperationException("不應呼叫上櫃歷史 provider"));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("2330", 101m)]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(2, result.ProcessedInstrumentCount);
        Assert.Equal(1, result.SuccessfulInstrumentCount);
        Assert.Equal(1, result.FailedInstrumentCount);
        Assert.Equal("AmbiguousMarket", result.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal(StockMarket.Unknown,
            await db.Stocks.Where(stock => stock.Market == StockMarket.Unknown).Select(stock => stock.Market).SingleAsync());
    }

    /// <summary>驗證已知市場與唯一辨識未知市場同代號時，兩個 execution target key 不會碰撞。</summary>
    [Fact]
    public async Task SyncAsync_UsesDistinctTargetKeysForMixedUniqueMarket()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("已知市場", "2330", StockMarket.Twse, null),
            CreateStock("待辨識", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((market, symbol, _, _) =>
            Success(symbol, market == StockMarket.Twse ? "2330.TW" : "2330.TWO", 100m));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(2, result.TargetCount);
        Assert.Equal(["Twse:2330", "Unknown:2330"], result.TargetKeys);
        Assert.Equal(["Twse:2330", "Unknown:2330"], result.SuccessfulTargetKeys);
    }

    /// <summary>驗證 retry 以 frozen Stock ID 保留辨識前的兩個原始 target 身分。</summary>
    [Fact]
    public async Task SyncAsync_PreservesOriginalMixedTargetKeysAcrossAttempts()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("已知市場", "2330", StockMarket.Twse, null),
            CreateStock("待辨識", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var requestCount = 0;
        var provider = new FakeProvider((_, symbol, _, _) =>
        {
            requestCount++;
            if (requestCount == 1)
                throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
            return Success(symbol, "2330.TW", 100m);
        });
        var synchronizer = new HistoricalMarketDataSynchronizer(
            db,
            provider,
            catalogService: new FakeCatalogService(CreateCatalog(
                [new CurrentPriceRecord("2330", 100m)],
                [new CurrentPriceRecord("6488", 88m)])));

        var first = await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));
        var second = await synchronizer.SyncAsync(
            new DateOnly(2026, 8, 7),
            frozenTargetKeys: first.TargetKeys);

        Assert.Equal(["Twse:2330", "Unknown:2330"], first.TargetKeys);
        Assert.Equal(["Twse:2330", "Unknown:2330"], second.TargetKeys);
        Assert.Equal(["Twse:2330", "Unknown:2330"], second.SuccessfulTargetKeys);
        Assert.Equal(2, second.TargetCount);
        Assert.Equal(2, second.SuccessfulInstrumentCount);
        Assert.Equal(2, provider.Requests.Count);
    }

    /// <summary>驗證使用者改變 frozen Unknown member 時不會被同代號失敗狀態誤認為自動辨識。</summary>
    [Fact]
    public async Task SyncAsync_KeepsManuallyChangedUnknownTargetFailedAcrossAttempts()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var known = CreateStock("已知市場", "2330", StockMarket.Twse, null);
        var changed = CreateStock("使用者改選", "2330", StockMarket.Unknown, null);
        db.Stocks.AddRange(known, changed);
        await db.SaveChangesAsync();
        var changedId = changed.Id;
        var requestCount = 0;
        var provider = new FakeProvider((_, symbol, _, _) =>
        {
            requestCount++;
            if (requestCount == 1)
                throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
            return Success(symbol, "2330.TW", 100m);
        });
        var catalog = new FakeCatalogService(
            CreateCatalog(
                [new CurrentPriceRecord("2330", 100m)],
                [new CurrentPriceRecord("6488", 88m)]),
            () =>
            {
                var stock = db.Stocks.Single(item => item.Id == changedId);
                stock.Market = StockMarket.Twse;
                db.SaveChanges();
            });
        var synchronizer = new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog);

        var first = await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));
        var second = await synchronizer.SyncAsync(
            new DateOnly(2026, 8, 7),
            frozenTargetKeys: first.TargetKeys);

        Assert.Equal("TargetChanged", first.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal(["Twse:2330", "Unknown:2330"], second.TargetKeys);
        Assert.Equal(["Twse:2330"], second.SuccessfulTargetKeys);
        Assert.Equal("TargetChanged", second.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal(2, second.TargetCount);
        Assert.Equal(1, second.SuccessfulInstrumentCount);
        Assert.Equal(1, second.FailedInstrumentCount);
    }

    /// <summary>驗證官方唯一命中後先保存市場，即使 Yahoo 歷史失敗也保留明確市場警告。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SyncAsync_KeepsDetectedMarketStateWhenHistoryProviderFails(int holdingCount)
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        for (var index = 0; index < holdingCount; index++)
            db.Stocks.Add(CreateStock($"歷史失敗 {index}", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var stockIds = await db.Stocks.OrderBy(stock => stock.Id).Select(stock => stock.Id).ToArrayAsync();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時"));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.True(result.RetryableFailure);
        Assert.Equal(0, result.SuccessfulInstrumentCount);
        Assert.Equal(1, result.FailedInstrumentCount);
        Assert.Equal(holdingCount, result.AffectedCount);
        Assert.Equal(stockIds.Select(stockId => $"stock:{stockId}"), result.AffectedRowKeys);
        Assert.All(await db.Stocks.ToListAsync(), stock => Assert.Equal(StockMarket.Twse, stock.Market));
        var state = await db.HistoricalPriceSyncStates.SingleAsync();
        Assert.Equal(StockMarket.Twse, state.Market);
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError, state.Status);
        Assert.Null(state.LastSucceededAtUtc);
        Assert.Empty(await db.HistoricalAdjustedPrices.ToListAsync());
    }

    /// <summary>驗證歷史 provider 請求期間使用者改選市場時不會提交過期辨識結果。</summary>
    [Fact]
    public async Task SyncAsync_RejectsUnknownTargetChangedDuringHistoryRequest()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.Add(CreateStock("市場競態", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, symbol, _, _) =>
        {
            var stock = db.Stocks.Single();
            stock.Market = StockMarket.Tpex;
            db.SaveChanges();
            return Success(symbol, "2330.TW", 100m);
        });
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("TargetChanged", result.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal(StockMarket.Tpex, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        Assert.Empty(await db.HistoricalPriceSyncStates.ToListAsync());
        Assert.Empty(await db.HistoricalAdjustedPrices.ToListAsync());
    }

    /// <summary>驗證 target changed 不會把 Twse request failure 寫入既有 Tpex 成功狀態。</summary>
    [Fact]
    public async Task SyncAsync_DoesNotOverwriteOtherMarketStateWhenResolvedTargetChanges()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("市場競態", "2330", StockMarket.Unknown, null);
        var lastSucceeded = DateTime.SpecifyKind(new DateTime(2026, 8, 6, 15, 0, 0), DateTimeKind.Utc);
        var latestTradingDate = new DateOnly(2026, 8, 6);
        db.Stocks.Add(stock);
        db.HistoricalPriceSyncStates.Add(new HistoricalPriceSyncState
        {
            Market = StockMarket.Tpex,
            Symbol = "2330",
            LastAttemptedAtUtc = lastSucceeded,
            LastSucceededAtUtc = lastSucceeded,
            LatestTradingDate = latestTradingDate,
            Status = HistoricalPriceSyncStatus.Success,
        });
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        var provider = new FakeProvider((_, symbol, _, _) =>
        {
            var current = db.Stocks.Single(item => item.Id == stockId);
            current.Market = StockMarket.Tpex;
            db.SaveChanges();
            return Success(symbol, "2330.TW", 100m);
        });
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("TargetChanged", result.FailedTargetCodes!["Unknown:2330"]);
        var state = await db.HistoricalPriceSyncStates.SingleAsync();
        Assert.Equal(StockMarket.Tpex, state.Market);
        Assert.Equal(HistoricalPriceSyncStatus.Success, state.Status);
        Assert.Equal(lastSucceeded, state.LastSucceededAtUtc);
        Assert.Equal(latestTradingDate, state.LatestTradingDate);
        Assert.Empty(await db.HistoricalAdjustedPrices.ToListAsync());
    }

    /// <summary>驗證同代號 Twse provider failure 不會覆寫另一個 Tpex target 的成功狀態。</summary>
    [Fact]
    public async Task SyncAsync_WritesProviderFailureToRequestedMarketStateOnly()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("上市", "2330", StockMarket.Twse, null),
            CreateStock("上櫃", "2330", StockMarket.Tpex, null));
        var lastSucceeded = DateTime.SpecifyKind(new DateTime(2026, 8, 6, 15, 0, 0), DateTimeKind.Utc);
        db.HistoricalPriceSyncStates.Add(new HistoricalPriceSyncState
        {
            Market = StockMarket.Tpex,
            Symbol = "2330",
            LastAttemptedAtUtc = lastSucceeded,
            LastSucceededAtUtc = lastSucceeded,
            LatestTradingDate = new DateOnly(2026, 8, 6),
            Status = HistoricalPriceSyncStatus.Success,
        });
        await db.SaveChangesAsync();
        var tpexStatePreservedBeforeOwnRequest = false;
        var provider = new FakeProvider((market, symbol, _, _) =>
        {
            if (market == StockMarket.Twse)
                throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");

            var state = db.HistoricalPriceSyncStates.Single(item => item.Market == StockMarket.Tpex);
            tpexStatePreservedBeforeOwnRequest = state.Status == HistoricalPriceSyncStatus.Success
                && state.LastSucceededAtUtc == lastSucceeded;
            return Success(symbol, "2330.TWO", 88m);
        });

        var result = await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog([], [])))
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("ProviderUnavailable", result.FailedTargetCodes!["Twse:2330"]);
        var tpexState = await db.HistoricalPriceSyncStates.SingleAsync(state => state.Market == StockMarket.Tpex);
        Assert.Equal(HistoricalPriceSyncStatus.Success, tpexState.Status);
        Assert.True(tpexStatePreservedBeforeOwnRequest);
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError,
            (await db.HistoricalPriceSyncStates.SingleAsync(state => state.Market == StockMarket.Twse)).Status);
    }

    /// <summary>驗證已知市場重複持股只要仍有 frozen member 符合身分就保存歷史資料。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncAsync_SucceedsKnownTargetWhenSiblingLeavesDuringHistoryRequest(bool deleteChangedHolding)
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var changed = CreateStock("第一筆", "2330", StockMarket.Twse, "甲券商");
        var survivor = CreateStock("第二筆", "2330", StockMarket.Twse, "乙券商");
        db.Stocks.AddRange(changed, survivor);
        await db.SaveChangesAsync();
        var changedId = changed.Id;
        var survivorId = survivor.Id;
        var provider = new FakeProvider((_, symbol, _, _) =>
        {
            var current = db.Stocks.Single(stock => stock.Id == changedId);
            if (deleteChangedHolding)
                db.Stocks.Remove(current);
            else
                current.Symbol = "1101";
            db.SaveChanges();
            return Success(symbol, "2330.TW", 100m);
        });
        var synchronizer = new HistoricalMarketDataSynchronizer(
            db,
            provider,
            catalogService: new FakeCatalogService(CreateCatalog([], [])));

        var result = await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(1, result.TargetCount);
        Assert.Equal(1, result.SuccessfulInstrumentCount);
        Assert.Equal(0, result.FailedInstrumentCount);
        Assert.Equal(["Twse:2330"], result.SuccessfulTargetKeys);
        Assert.True(await db.Stocks.AnyAsync(stock => stock.Id == survivorId
            && stock.Market == StockMarket.Twse
            && stock.Symbol == "2330"));
        Assert.Equal(100m,
            await db.HistoricalAdjustedPrices
                .Where(price => price.Market == StockMarket.Twse && price.Symbol == "2330")
                .Select(price => price.AdjustedClose)
                .SingleAsync());
    }

    /// <summary>驗證未知市場重複持股只更新 catalog 回應期間仍符合原始 target 的成員。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncAsync_ResolvesUnknownTargetWhenSiblingLeavesDuringCatalogRequest(bool deleteChangedHolding)
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var changed = CreateStock("第一筆", "2330", StockMarket.Unknown, "甲券商");
        var survivor = CreateStock("第二筆", "2330", StockMarket.Unknown, "乙券商");
        db.Stocks.AddRange(changed, survivor);
        await db.SaveChangesAsync();
        var changedId = changed.Id;
        var survivorId = survivor.Id;
        var provider = new FakeProvider((market, symbol, _, _) =>
            market == StockMarket.Twse && symbol == "2330"
                ? Success(symbol, "2330.TW", 100m)
                : throw new InvalidOperationException("不應呼叫其他歷史市場"));
        var catalog = new FakeCatalogService(
            CreateCatalog(
                [new CurrentPriceRecord("2330", 100m)],
                [new CurrentPriceRecord("6488", 88m)]),
            () =>
            {
                var current = db.Stocks.Single(stock => stock.Id == changedId);
                if (deleteChangedHolding)
                    db.Stocks.Remove(current);
                else
                    current.Symbol = "1101";
                db.SaveChanges();
            });
        var synchronizer = new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog);

        var result = await synchronizer.SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal(1, result.TargetCount);
        Assert.Equal(1, result.SuccessfulInstrumentCount);
        Assert.Equal(0, result.FailedInstrumentCount);
        Assert.Equal(["Unknown:2330"], result.SuccessfulTargetKeys);
        Assert.Equal(StockMarket.Twse,
            await db.Stocks.Where(stock => stock.Id == survivorId).Select(stock => stock.Market).SingleAsync());
        if (!deleteChangedHolding)
            Assert.Equal(StockMarket.Unknown,
                await db.Stocks.Where(stock => stock.Id == changedId).Select(stock => stock.Market).SingleAsync());
        Assert.Single(provider.Requests);
        Assert.Equal(100m, await db.HistoricalAdjustedPrices.Select(price => price.AdjustedClose).SingleAsync());
    }

    /// <summary>驗證只有自動辨識更新的 frozen IDs 可在 Yahoo 回應時證明 Unknown target 仍存活。</summary>
    [Fact]
    public async Task SyncAsync_DoesNotLetManuallyChangedSiblingPreserveResolvedUnknownTarget()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var automatic = CreateStock("自動辨識", "2330", StockMarket.Unknown, null);
        var manual = CreateStock("使用者改選", "2330", StockMarket.Unknown, null);
        db.Stocks.AddRange(automatic, manual);
        await db.SaveChangesAsync();
        var automaticId = automatic.Id;
        var manualId = manual.Id;
        var catalog = new FakeCatalogService(
            CreateCatalog(
                [new CurrentPriceRecord("2330", 100m)],
                [new CurrentPriceRecord("6488", 88m)]),
            () =>
            {
                var stock = db.Stocks.Single(item => item.Id == manualId);
                stock.Market = StockMarket.Twse;
                db.SaveChanges();
            });
        var provider = new FakeProvider((_, symbol, _, _) =>
        {
            db.Stocks.Remove(db.Stocks.Single(item => item.Id == automaticId));
            db.SaveChanges();
            return Success(symbol, "2330.TW", 100m);
        });

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("TargetChanged", result.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal(0, result.SuccessfulInstrumentCount);
        Assert.Equal(1, result.FailedInstrumentCount);
        Assert.Empty(await db.HistoricalAdjustedPrices.ToListAsync());
        Assert.Equal(StockMarket.Twse,
            await db.Stocks.Where(stock => stock.Id == manualId).Select(stock => stock.Market).SingleAsync());
    }

    /// <summary>驗證全部 frozen members 離開未知 target 時保留永久失敗並清除追蹤狀態。</summary>
    [Fact]
    public async Task SyncAsync_ReturnsTargetChangedAndClearsTrackingWhenAllUnknownMembersLeave()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("離開目標", "2330", StockMarket.Unknown, null);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        db.ChangeTracker.Clear();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new InvalidOperationException("不應呼叫歷史 provider"));
        var catalog = new FakeCatalogService(
            CreateCatalog(
                [new CurrentPriceRecord("2330", 100m)],
                [new CurrentPriceRecord("6488", 88m)]),
            () =>
            {
                var current = db.Stocks.Single(item => item.Id == stockId);
                current.Symbol = "1101";
                db.SaveChanges();
            });

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("TargetChanged", result.ResultCode);
        Assert.Equal("TargetChanged", result.FailedTargetCodes!["Unknown:2330"]);
        Assert.False(result.RetryableFailure);
        Assert.Empty(provider.Requests);
        Assert.Empty(await db.HistoricalPriceSyncStates.ToListAsync());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>驗證多個 frozen targets 全部變更時依傳入 frozen order 回傳結果。</summary>
    [Fact]
    public async Task SyncAsync_PreservesFrozenOrderWhenAllTargetsChanged()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("第一標的", "1111", StockMarket.Twse, null),
            CreateStock("第二標的", "2222", StockMarket.Tpex, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new InvalidOperationException("全部 target 已變更時不應呼叫 provider"));

        var result = await new HistoricalMarketDataSynchronizer(
                db,
                provider,
                catalogService: new FakeCatalogService(CreateCatalog([], [])))
            .SyncAsync(
                new DateOnly(2026, 8, 7),
                frozenTargetKeys: ["Twse:9999", "Tpex:8888", "Unknown:7777"]);

        Assert.Equal(["Twse:9999", "Tpex:8888", "Unknown:7777"], result.TargetKeys);
        Assert.Equal(3, result.FailedTargetCodes!.Count);
        Assert.All(result.FailedTargetCodes.Values, code => Assert.Equal("TargetChanged", code));
        Assert.Empty(provider.Requests);
    }

    /// <summary>驗證市場 persistence 失敗會 rollback 並以乾淨 tracker 保存 Unknown 安全狀態。</summary>
    [Fact]
    public async Task SyncAsync_PersistsSafeUnknownStateAfterMarketPersistenceFailure()
    {
        await using var connection = await OpenConnectionAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new FailingSaveDbContext(options);
        db.Database.EnsureCreated();
        db.Stocks.Add(CreateStock("市場寫入失敗", "2330", StockMarket.Unknown, null));
        await db.SaveChangesAsync();
        db.FailNextSave();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new InvalidOperationException("市場 persistence 失敗後不應呼叫 provider"));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var result = await new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
            .SyncAsync(new DateOnly(2026, 8, 7));

        Assert.Equal("DatabaseFailure", result.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal("DatabaseFailure", result.ResultCode);
        Assert.False(result.RetryableFailure);
        Assert.Equal(StockMarket.Unknown, await db.Stocks.Select(stock => stock.Market).SingleAsync());
        var state = await db.HistoricalPriceSyncStates.SingleAsync();
        Assert.Equal(StockMarket.Unknown, state.Market);
        Assert.Equal(HistoricalPriceSyncStatus.ProviderError, state.Status);
        Assert.Equal("歷史行情市場保存失敗", state.SafeMessage);
        Assert.Empty(provider.Requests);
    }

    /// <summary>驗證失敗狀態保存遇到 SQLite busy 時攜帶已提交市場 row 與完整 target 的 partial result。</summary>
    [Fact]
    public async Task SyncAsync_ThrowsPartialResultWhenFailureStatePersistenceFails()
    {
        await using var connection = await OpenConnectionAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new FailingSaveDbContext(options);
        db.Database.EnsureCreated();
        var stock = CreateStock("待重試", "2330", StockMarket.Unknown, null);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        db.FailNextHistoricalFailureStateSave();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時"));
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var exception = await Record.ExceptionAsync(() =>
            new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
                .SyncAsync(new DateOnly(2026, 8, 7)));

        Assert.NotNull(exception);
        Assert.Equal("HistoricalMarketDataPartialFailureException", exception.GetType().Name);
        var partialProperty = exception.GetType().GetProperty("PartialResult");
        Assert.NotNull(partialProperty);
        var partial = Assert.IsType<HistoricalMarketDataSyncResult>(partialProperty.GetValue(exception));
        Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(5, ((SqliteException)exception.InnerException).SqliteErrorCode);
        Assert.Equal(1, partial.TargetCount);
        Assert.Equal(["Unknown:2330"], partial.TargetKeys);
        Assert.Empty(partial.SuccessfulTargetKeys!);
        Assert.Equal("DatabaseBusy", partial.FailedTargetCodes!["Unknown:2330"]);
        Assert.True(partial.RetryableFailure);
        Assert.Equal(["stock:" + stockId], partial.AffectedRowKeys);
        Assert.Equal(StockMarket.Twse, await db.Stocks.Select(item => item.Market).SingleAsync());
    }

    /// <summary>驗證中止時所有尚未成功的 pending targets 都取得相同 bounded cause disposition。</summary>
    [Fact]
    public async Task SyncAsync_FillsPendingTargetsWhenFailureStatePersistenceStopsAttempt()
    {
        await using var connection = await OpenConnectionAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new FailingSaveDbContext(options);
        db.Database.EnsureCreated();
        db.Stocks.AddRange(
            CreateStock("第一標的", "1111", StockMarket.Twse, null),
            CreateStock("第二標的", "2222", StockMarket.Twse, null));
        await db.SaveChangesAsync();
        db.FailNextHistoricalFailureStateSave();
        var provider = new FakeProvider((_, _, _, _) =>
            throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時"));

        var exception = await Record.ExceptionAsync(() =>
            new HistoricalMarketDataSynchronizer(
                    db,
                    provider,
                    catalogService: new FakeCatalogService(CreateCatalog([], [])))
                .SyncAsync(new DateOnly(2026, 8, 7)));

        var partial = GetPartialResult(exception);
        Assert.Equal(2, partial.ProcessedInstrumentCount);
        Assert.Equal(2, partial.TargetCount);
        Assert.Equal(0, partial.SuccessfulInstrumentCount);
        Assert.Equal(2, partial.FailedInstrumentCount);
        Assert.Equal(["Twse:1111", "Twse:2222"], partial.TargetKeys);
        Assert.Empty(partial.SuccessfulTargetKeys!);
        Assert.Equal(2, partial.FailedTargetCodes!.Count);
        Assert.All(partial.FailedTargetCodes.Values, code => Assert.Equal("DatabaseBusy", code));
        Assert.Equal("DatabaseBusy", partial.ResultCode);
        Assert.True(partial.RetryableFailure);
        Assert.IsType<SqliteException>(exception!.InnerException);
    }

    /// <summary>驗證 target identity 查詢 SQLite busy 會轉為包含完整 target universe 的 partial failure。</summary>
    [Fact]
    public async Task SyncAsync_WrapsIdentityQueryDatabaseBusyAsPartialFailure()
    {
        await using var connection = await OpenConnectionAsync();
        var interceptor = new IdentityQueryFailureInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        db.Stocks.AddRange(
            CreateStock("第一標的", "1111", StockMarket.Twse, null),
            CreateStock("第二標的", "2222", StockMarket.Twse, null));
        await db.SaveChangesAsync();
        var provider = new FakeProvider((_, _, _, _) =>
        {
            interceptor.Arm();
            throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
        });

        var exception = await Record.ExceptionAsync(() =>
            new HistoricalMarketDataSynchronizer(
                    db,
                    provider,
                    catalogService: new FakeCatalogService(CreateCatalog([], [])))
                .SyncAsync(new DateOnly(2026, 8, 7)));

        var partial = GetPartialResult(exception);
        Assert.Equal(2, partial.TargetCount);
        Assert.Equal(["Twse:1111", "Twse:2222"], partial.TargetKeys);
        Assert.Equal(2, partial.FailedInstrumentCount);
        Assert.All(partial.FailedTargetCodes!.Values, code => Assert.Equal("DatabaseBusy", code));
        Assert.True(partial.RetryableFailure);
        var sqlite = Assert.IsType<SqliteException>(exception!.InnerException);
        Assert.Equal(5, sqlite.SqliteErrorCode);
    }

    /// <summary>驗證第二個 target 取消時 partial result 保留第一個 target 已提交的成功與 row key。</summary>
    [Fact]
    public async Task SyncAsync_PreservesCommittedProgressWhenHostCancellationStopsAttempt()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        db.Stocks.AddRange(
            CreateStock("第一標的", "1111", StockMarket.Twse, null),
            CreateStock("第二標的", "2222", StockMarket.Twse, null));
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var provider = new SuccessThenCancelingProvider(cancellation);

        var exception = await Record.ExceptionAsync(() =>
            new HistoricalMarketDataSynchronizer(
                    db,
                    provider,
                    catalogService: new FakeCatalogService(CreateCatalog([], [])))
                .SyncAsync(new DateOnly(2026, 8, 7), cancellation.Token));

        var partial = GetPartialResult(exception);
        var successfulKey = $"Twse:{provider.SuccessfulSymbol}";
        var affectedKey = $"Twse:{provider.SuccessfulSymbol}:2026-08-06";
        Assert.Equal(2, partial.TargetCount);
        Assert.Equal(1, partial.SuccessfulInstrumentCount);
        Assert.Equal(1, partial.FailedInstrumentCount);
        Assert.Equal([successfulKey], partial.SuccessfulTargetKeys);
        Assert.Equal([affectedKey], partial.AffectedRowKeys);
        var failedCodes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(partial.FailedTargetCodes);
        Assert.Single(failedCodes);
        Assert.Equal("Canceled", failedCodes.Single().Value);
        Assert.False(partial.RetryableFailure);
        Assert.IsAssignableFrom<OperationCanceledException>(exception!.InnerException);
        Assert.Equal(1, await db.HistoricalAdjustedPrices.CountAsync());
        var states = await db.HistoricalPriceSyncStates.ToListAsync();
        Assert.Single(states);
        Assert.Equal(HistoricalPriceSyncStatus.Success, states[0].Status);
    }

    /// <summary>驗證 Unknown 市場提交後取消時 partial result 保留已提交的 stock row key。</summary>
    [Fact]
    public async Task SyncAsync_PreservesCommittedMarketRowWhenHostCancellationStopsUnknownTarget()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDb(connection);
        var stock = CreateStock("待辨識", "2330", StockMarket.Unknown, null);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        var stockId = stock.Id;
        using var cancellation = new CancellationTokenSource();
        var provider = new CancelingProvider(cancellation);
        var catalog = new FakeCatalogService(CreateCatalog(
            [new CurrentPriceRecord("2330", 100m)],
            [new CurrentPriceRecord("6488", 88m)]));

        var exception = await Record.ExceptionAsync(() =>
            new HistoricalMarketDataSynchronizer(db, provider, catalogService: catalog)
                .SyncAsync(new DateOnly(2026, 8, 7), cancellation.Token));

        var partial = GetPartialResult(exception);
        Assert.Equal(["Unknown:2330"], partial.TargetKeys);
        Assert.Equal("Canceled", partial.FailedTargetCodes!["Unknown:2330"]);
        Assert.Equal(["stock:" + stockId], partial.AffectedRowKeys);
        Assert.IsAssignableFrom<OperationCanceledException>(exception!.InnerException);
        Assert.Equal(StockMarket.Twse, await db.Stocks.Select(item => item.Market).SingleAsync());
        Assert.Empty(await db.HistoricalPriceSyncStates.ToListAsync());
    }

    /// <summary>從 bounded partial exception 取得 execution-local 同步結果。</summary>
    private static HistoricalMarketDataSyncResult GetPartialResult(Exception? exception)
    {
        Assert.NotNull(exception);
        Assert.Equal("HistoricalMarketDataPartialFailureException", exception.GetType().Name);
        var partialProperty = exception.GetType().GetProperty("PartialResult");
        Assert.NotNull(partialProperty);
        return Assert.IsType<HistoricalMarketDataSyncResult>(partialProperty.GetValue(exception));
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

    /// <summary>建立測試用完整官方雙市場 catalog snapshot。</summary>
    private static OfficialMarketCatalogSnapshot CreateCatalog(
        IReadOnlyList<CurrentPriceRecord> twse,
        IReadOnlyList<CurrentPriceRecord> tpex)
        => new(
            CurrentPriceProviderResult.Success("TWSE", twse),
            CurrentPriceProviderResult.Success("TPEx", tpex));

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

    /// <summary>第一個 request 成功、第二個 request 取消 host token 的歷史 provider。</summary>
    private sealed class SuccessThenCancelingProvider : IHistoricalAdjustedPriceProvider
    {
        private readonly CancellationTokenSource _cancellation;
        private int _callCount;

        /// <summary>初始化可控制 host cancellation 的 provider。</summary>
        public SuccessThenCancelingProvider(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        /// <summary>取得第一個成功 request 的代號。</summary>
        public string? SuccessfulSymbol { get; private set; }

        /// <summary>第一次回傳成功資料，第二次取消並拋出 OperationCanceledException。</summary>
        public Task<HistoricalPriceProviderResult> GetPricesAsync(
            StockMarket market,
            string symbol,
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                SuccessfulSymbol = symbol;
                return Task.FromResult(Success(symbol, symbol + ".TW", 100m));
            }

            _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("取消後不應繼續執行");
        }
    }

    /// <summary>在 request 時取消 host token 的歷史 provider。</summary>
    private sealed class CancelingProvider : IHistoricalAdjustedPriceProvider
    {
        private readonly CancellationTokenSource _cancellation;

        /// <summary>初始化可控制 host cancellation 的 provider。</summary>
        public CancelingProvider(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        /// <summary>取消傳入 token 並拋出 OperationCanceledException。</summary>
        public Task<HistoricalPriceProviderResult> GetPricesAsync(
            StockMarket market,
            string symbol,
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default)
        {
            _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("取消後不應繼續執行");
        }
    }

    /// <summary>可在 provider failure 後精準讓下一個 Stocks identity query 拋 SQLite busy。</summary>
    private sealed class IdentityQueryFailureInterceptor : DbCommandInterceptor
    {
        private int _armed;

        /// <summary>安排下一個 Stocks reader query 拋出 SQLite busy。</summary>
        public void Arm()
        {
            Interlocked.Exchange(ref _armed, 1);
        }

        /// <summary>在非同步 reader query 執行前注入一次 SQLite busy。</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <summary>只在已 armed 的 Stocks query 注入一次 SQLite busy。</summary>
        private void ThrowIfArmed(DbCommand command)
        {
            if (!command.CommandText.Contains("Stocks", StringComparison.Ordinal)
                || Interlocked.Exchange(ref _armed, 0) == 0)
                return;
            throw new SqliteException("database is locked", 5);
        }
    }

    /// <summary>提供可控制官方市場 snapshot 與呼叫次數的測試服務。</summary>
    private sealed class FakeCatalogService : IOfficialMarketCatalogService
    {
        private readonly Action? _beforeFetch;

        /// <summary>初始化固定官方市場 snapshot。</summary>
        public FakeCatalogService(OfficialMarketCatalogSnapshot snapshot, Action? beforeFetch = null)
        {
            Snapshot = snapshot;
            _beforeFetch = beforeFetch;
        }

        /// <summary>取得固定官方市場 snapshot。</summary>
        public OfficialMarketCatalogSnapshot Snapshot { get; }

        /// <summary>取得官方市場 snapshot 呼叫次數。</summary>
        public int FetchCount { get; private set; }

        /// <summary>回傳固定官方市場 snapshot。</summary>
        public Task<OfficialMarketCatalogSnapshot> FetchAsync(CancellationToken cancellationToken = default)
        {
            FetchCount++;
            _beforeFetch?.Invoke();
            return Task.FromResult(Snapshot);
        }

        /// <summary>以純 resolver 回傳單一代號測試結果。</summary>
        public Task<OfficialMarketResolution> LookupAsync(string? symbol, CancellationToken cancellationToken = default)
            => Task.FromResult(OfficialMarketCatalogResolver.Resolve(Snapshot, symbol));
    }

    /// <summary>提供可注入下一次 SaveChanges failure 的歷史同步測試 context。</summary>
    private sealed class FailingSaveDbContext : AppDbContext
    {
        private bool _failNextSave;
        private bool _failNextHistoricalFailureStateSave;

        /// <summary>初始化使用測試 SQLite options 的 failure-injection context。</summary>
        public FailingSaveDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        /// <summary>安排下一次非同步保存拋出永久 persistence failure。</summary>
        public void FailNextSave()
        {
            _failNextSave = true;
        }

        /// <summary>安排下一次歷史失敗狀態保存拋出可重試的 SQLite busy。</summary>
        public void FailNextHistoricalFailureStateSave()
        {
            _failNextHistoricalFailureStateSave = true;
        }

        /// <summary>僅讓下一次保存失敗，後續安全狀態保存可正常執行。</summary>
        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            if (_failNextHistoricalFailureStateSave
                && ChangeTracker.Entries<HistoricalPriceSyncState>().Any(entry =>
                    entry.State is EntityState.Added or EntityState.Modified
                    && entry.Entity.Status == HistoricalPriceSyncStatus.ProviderError))
            {
                _failNextHistoricalFailureStateSave = false;
                throw new SqliteException("database is locked", 5);
            }
            if (_failNextSave)
            {
                _failNextSave = false;
                throw new InvalidOperationException("Injected market persistence failure");
            }

            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    /// <summary>保存 fake provider 的單次請求參數。</summary>
    private sealed record ProviderRequest(
        StockMarket Market,
        string Symbol,
        DateOnly StartDate,
        DateOnly EndDate);
}
