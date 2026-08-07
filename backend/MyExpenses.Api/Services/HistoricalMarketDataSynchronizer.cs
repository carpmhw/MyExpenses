using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>描述一次夜間歷史行情批次的處理摘要。</summary>
public sealed record HistoricalMarketDataSyncResult(
    int ProcessedInstrumentCount,
    int SuccessfulInstrumentCount,
    int FailedInstrumentCount);

/// <summary>協調目前持股、provider、歷史 upsert 與逐標的同步狀態。</summary>
public sealed class HistoricalMarketDataSynchronizer
{
    private static readonly TimeZoneInfo TaiwanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private readonly AppDbContext _db;
    private readonly IHistoricalAdjustedPriceProvider _provider;
    private readonly ILogger<HistoricalMarketDataSynchronizer>? _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化使用 scoped DbContext 的歷史行情同步協調器。</summary>
    public HistoricalMarketDataSynchronizer(
        AppDbContext db,
        IHistoricalAdjustedPriceProvider provider,
        ILogger<HistoricalMarketDataSynchronizer>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _provider = provider;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>正規化持股代號，作為跨券商歷史行情的穩定身分。</summary>
    public static string NormalizeSymbol(string symbol)
        => symbol.Trim().ToUpperInvariant();

    /// <summary>依指定台灣日期同步目前持股的滾動 13 個月歷史行情。</summary>
    public async Task<HistoricalMarketDataSyncResult> SyncAsync(
        DateOnly? asOfDate = null,
        CancellationToken cancellationToken = default)
    {
        var endDate = asOfDate ?? GetTaiwanDate();
        var startDate = endDate.AddMonths(-13);
        var stocks = await _db.Stocks.AsNoTracking().ToListAsync(cancellationToken);
        var currentStocks = stocks
            .Where(stock => !string.IsNullOrWhiteSpace(stock.Symbol))
            .Select(stock => new StockSyncCandidate(stock, NormalizeSymbol(stock.Symbol)))
            .ToList();

        var processed = 0;
        var succeeded = 0;
        var failed = 0;

        foreach (var group in currentStocks
                     .Where(candidate => candidate.Stock.Market is not StockMarket.Unknown)
                     .GroupBy(candidate => (candidate.Stock.Market, candidate.Symbol)))
        {
            processed++;
            if (await SyncExplicitInstrumentAsync(
                    group.Key.Market,
                    group.Key.Symbol,
                    startDate,
                    endDate,
                    cancellationToken))
                succeeded++;
            else
                failed++;
        }

        foreach (var group in currentStocks
                     .Where(candidate => candidate.Stock.Market is StockMarket.Unknown)
                     .GroupBy(candidate => candidate.Symbol))
        {
            processed++;
            if (await DetectAndSyncUnknownInstrumentAsync(
                    group.Key,
                    startDate,
                    endDate,
                    cancellationToken))
                succeeded++;
            else
                failed++;
        }

        return new HistoricalMarketDataSyncResult(processed, succeeded, failed);
    }

    /// <summary>同步已由使用者指定市場的單一標的並隔離 provider 失敗。</summary>
    private async Task<bool> SyncExplicitInstrumentAsync(
        StockMarket market,
        string symbol,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _provider.GetPricesAsync(
                market,
                symbol,
                startDate,
                endDate,
                cancellationToken);
            await PersistSuccessAsync(market, symbol, result, startDate, endDate, false, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HistoricalPriceProviderException exception)
        {
            await PersistFailureAsync(market, symbol, MapStatus(exception.Code), exception.SafeMessage, cancellationToken);
            return false;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Historical market data synchronization failed for {Market}/{Symbol}", market, symbol);
            await PersistFailureAsync(market, symbol, HistoricalPriceSyncStatus.ProviderError, "歷史行情同步失敗", cancellationToken);
            return false;
        }
    }

    /// <summary>驗證未知市場的兩個候選並只在唯一成功時更新未知持股。</summary>
    private async Task<bool> DetectAndSyncUnknownInstrumentAsync(
        string symbol,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(StockMarket Market, HistoricalPriceProviderResult? Result, HistoricalPriceProviderException? Error)>();
        foreach (var market in new[] { StockMarket.Twse, StockMarket.Tpex })
        {
            try
            {
                var result = await _provider.GetPricesAsync(market, symbol, startDate, endDate, cancellationToken);
                candidates.Add(result.Prices.Count > 0
                    ? (market, result, null)
                    : (market, null, new HistoricalPriceProviderException("no_data", "沒有可用行情")));
            }
            catch (HistoricalPriceProviderException exception)
            {
                candidates.Add((market, null, exception));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                candidates.Add((market, null, new HistoricalPriceProviderException("provider_error", "歷史行情同步失敗")));
            }
        }

        var valid = candidates.Where(candidate => candidate.Result is not null).ToList();
        var uncertainCandidate = candidates.Any(candidate =>
            candidate.Result is null
            && candidate.Error is not null
            && !IsDefinitiveCandidateFailure(candidate.Error));
        if (valid.Count == 1 && !uncertainCandidate)
        {
            var candidate = valid[0];
            try
            {
                await PersistSuccessAsync(
                    candidate.Market,
                    symbol,
                    candidate.Result!,
                    startDate,
                    endDate,
                    true,
                    cancellationToken);
                return true;
            }
            catch (HistoricalPriceProviderException exception)
            {
                await PersistFailureAsync(candidate.Market, symbol, MapStatus(exception.Code), exception.SafeMessage, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Detected market synchronization failed for {Market}/{Symbol}", candidate.Market, symbol);
                await PersistFailureAsync(candidate.Market, symbol, HistoricalPriceSyncStatus.ProviderError, "歷史行情同步失敗", cancellationToken);
            }

            return false;
        }

        if (valid.Count == 1 && uncertainCandidate)
        {
            await PersistFailureAsync(
                StockMarket.Unknown,
                symbol,
                HistoricalPriceSyncStatus.ProviderError,
                "市場辨識候選回應不完整，保留待辨識狀態",
                cancellationToken);
            return false;
        }

        if (valid.Count > 1)
        {
            await PersistFailureAsync(
                StockMarket.Unknown,
                symbol,
                HistoricalPriceSyncStatus.AmbiguousMarket,
                "市場辨識結果不唯一，請選擇交易市場",
                cancellationToken);
            return false;
        }

        var status = candidates.All(candidate => candidate.Error?.Code == "no_data")
            ? HistoricalPriceSyncStatus.NoData
            : HistoricalPriceSyncStatus.ProviderError;
        await PersistFailureAsync(
            StockMarket.Unknown,
            symbol,
            status,
            status == HistoricalPriceSyncStatus.NoData ? "找不到可驗證的交易市場" : "市場辨識服務暫時無法使用",
            cancellationToken);
        return false;
    }

    /// <summary>以單一 transaction upsert 歷史價格、同步狀態及市場辨識結果。</summary>
    private async Task PersistSuccessAsync(
        StockMarket market,
        string symbol,
        HistoricalPriceProviderResult result,
        DateOnly startDate,
        DateOnly endDate,
        bool updateUnknownStocks,
        CancellationToken cancellationToken)
    {
        var points = result.Prices
            .Where(point => point.TradingDate >= startDate
                && point.TradingDate <= endDate
                && point.AdjustedClose > 0m)
            .GroupBy(point => point.TradingDate)
            .Select(group => group.Last())
            .ToList();
        if (points.Count == 0)
            throw new HistoricalPriceProviderException("no_data", "歷史行情沒有可用價格");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (updateUnknownStocks)
        {
            var unknownStocks = await _db.Stocks
                .Where(stock => stock.Market == StockMarket.Unknown)
                .ToListAsync(cancellationToken);
            foreach (var stock in unknownStocks.Where(stock => NormalizeSymbol(stock.Symbol) == symbol))
                stock.Market = market;
        }

        var existing = await _db.HistoricalAdjustedPrices
            .Where(price => price.Market == market && price.Symbol == symbol)
            .ToListAsync(cancellationToken);
        var existingByDate = existing.ToDictionary(price => price.TradingDate);
        var fetchedAt = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        if (updateUnknownStocks)
        {
            var unknownState = await _db.HistoricalPriceSyncStates
                .SingleOrDefaultAsync(
                    state => state.Market == StockMarket.Unknown && state.Symbol == symbol,
                    cancellationToken);
            if (unknownState is not null)
                _db.HistoricalPriceSyncStates.Remove(unknownState);
        }
        foreach (var point in points)
        {
            if (existingByDate.TryGetValue(point.TradingDate, out var stored))
            {
                stored.AdjustedClose = point.AdjustedClose;
                stored.Provider = result.Provider;
                stored.FetchedAtUtc = fetchedAt;
            }
            else
            {
                _db.HistoricalAdjustedPrices.Add(new HistoricalAdjustedPrice
                {
                    Market = market,
                    Symbol = symbol,
                    TradingDate = point.TradingDate,
                    AdjustedClose = point.AdjustedClose,
                    Provider = result.Provider,
                    FetchedAtUtc = fetchedAt,
                });
            }
        }

        var state = await GetOrCreateStateAsync(market, symbol, cancellationToken);
        state.LastAttemptedAtUtc = fetchedAt;
        state.LastSucceededAtUtc = fetchedAt;
        state.LatestTradingDate = points.Max(point => point.TradingDate);
        state.Status = HistoricalPriceSyncStatus.Success;
        state.SafeMessage = null;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>保存安全失敗狀態但保留上次成功時間、截止日與歷史價格。</summary>
    private async Task PersistFailureAsync(
        StockMarket market,
        string symbol,
        HistoricalPriceSyncStatus status,
        string message,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var state = await GetOrCreateStateAsync(market, symbol, cancellationToken);
        state.LastAttemptedAtUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        state.Status = status;
        state.SafeMessage = message.Length > 500 ? message[..500] : message;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>取得或建立單一市場代號的同步狀態列。</summary>
    private async Task<HistoricalPriceSyncState> GetOrCreateStateAsync(
        StockMarket market,
        string symbol,
        CancellationToken cancellationToken)
    {
        var state = await _db.HistoricalPriceSyncStates
            .SingleOrDefaultAsync(item => item.Market == market && item.Symbol == symbol, cancellationToken);
        if (state is not null)
            return state;

        state = new HistoricalPriceSyncState
        {
            Market = market,
            Symbol = symbol,
            Status = HistoricalPriceSyncStatus.ProviderError,
        };
        _db.HistoricalPriceSyncStates.Add(state);
        return state;
    }

    /// <summary>將 provider 安全錯誤代碼映射到持久化同步狀態。</summary>
    private static HistoricalPriceSyncStatus MapStatus(string code)
        => code == "no_data"
            ? HistoricalPriceSyncStatus.NoData
            : code.StartsWith("invalid", StringComparison.Ordinal)
                ? HistoricalPriceSyncStatus.InvalidResponse
                : HistoricalPriceSyncStatus.ProviderError;

    /// <summary>判斷候選失敗是否足以證明該市場沒有可驗證行情。</summary>
    private static bool IsDefinitiveCandidateFailure(HistoricalPriceProviderException exception)
        => exception.Code == "no_data"
            || exception.Code.StartsWith("invalid", StringComparison.Ordinal);

    /// <summary>取得目前時間在台灣市場時區的日曆日期。</summary>
    private DateOnly GetTaiwanDate()
    {
        var utc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TaiwanTimeZone));
    }

    /// <summary>保存持股與正規化代號的同步候選。</summary>
    private sealed record StockSyncCandidate(Stock Stock, string Symbol);
}
