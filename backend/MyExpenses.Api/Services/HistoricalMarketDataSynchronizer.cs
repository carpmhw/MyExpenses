using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>描述一次夜間歷史行情批次的 typed aggregate 結果。</summary>
public sealed record HistoricalMarketDataSyncResult(
    int ProcessedInstrumentCount,
    int SuccessfulInstrumentCount,
    int FailedInstrumentCount,
    int AffectedInstrumentCount = 0,
    bool RetryableFailure = false,
    int? TargetCount = null,
    IReadOnlyCollection<string>? TargetKeys = null,
    IReadOnlyCollection<string>? SuccessfulTargetKeys = null,
    IReadOnlyDictionary<string, string>? FailedTargetCodes = null,
    IReadOnlyCollection<string>? AffectedRowKeys = null,
    string ResultCode = "Completed")
{
    /// <summary>取得 execution aggregate 使用的受影響 row 數量。</summary>
    public int AffectedCount => AffectedRowKeys?.Count ?? AffectedInstrumentCount;

    /// <summary>取得是否含有可交給 runner 重試的批次 failure。</summary>
    public bool HasRetryableFailure => RetryableFailure;
}

/// <summary>協調目前持股、provider、歷史 upsert 與逐標的同步狀態。</summary>
public sealed class HistoricalMarketDataSynchronizer
{
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
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>正規化持股代號，作為跨券商歷史行情的穩定身分。</summary>
    public static string NormalizeSymbol(string symbol)
        => symbol.Trim().ToUpperInvariant();

    /// <summary>依指定台灣日期同步目前持股的滾動 13 個月歷史行情。</summary>
    public async Task<HistoricalMarketDataSyncResult> SyncAsync(
        DateOnly? asOfDate = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? frozenTargetKeys = null)
    {
        var endDate = asOfDate ?? GetTaiwanDate();
        var startDate = endDate.AddMonths(-13);
        List<Stock> stocks;
        try
        {
            stocks = await _db.Stocks.AsNoTracking().ToListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var retryable = RetryClassification.IsRetryable(exception);
            return CreateEnumerationFailure(retryable ? "DatabaseBusy" : "DatabaseFailure", retryable);
        }

        var currentStocks = stocks
            .Where(stock => !string.IsNullOrWhiteSpace(stock.Symbol))
            .Select(stock => new StockSyncCandidate(stock, NormalizeSymbol(stock.Symbol)))
            .ToList();
        var frozenKeys = frozenTargetKeys is null
            ? null
            : frozenTargetKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .ToHashSet(StringComparer.Ordinal);
        if (frozenKeys is not null)
        {
            currentStocks = currentStocks
                .Where(candidate => IsFrozenTarget(candidate, frozenKeys))
                .ToList();
        }
        if (currentStocks.Count == 0)
        {
            return new HistoricalMarketDataSyncResult(
                0,
                0,
                0,
                ResultCode: "NoEligibleTargets",
                TargetCount: 0,
                TargetKeys: [],
                SuccessfulTargetKeys: [],
                FailedTargetCodes: new Dictionary<string, string>(StringComparer.Ordinal),
                AffectedRowKeys: []);
        }

        var knownMarketsBySymbol = currentStocks
            .Where(candidate => candidate.Stock.Market is not StockMarket.Unknown)
            .GroupBy(candidate => candidate.Symbol)
            .ToDictionary(
                group => group.Key,
                group => group.Select(candidate => candidate.Stock.Market).Distinct().ToHashSet(),
                StringComparer.Ordinal);
        var knownSymbols = knownMarketsBySymbol.Keys.ToHashSet(StringComparer.Ordinal);

        var targetKeys = new List<string>();
        var successfulKeys = new List<string>();
        var failedCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var affectedRows = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;
        var succeeded = 0;
        var failed = 0;
        var retryableFailure = false;

        foreach (var group in currentStocks
                     .Where(candidate => candidate.Stock.Market is not StockMarket.Unknown)
                     .GroupBy(candidate => (candidate.Stock.Market, candidate.Symbol)))
        {
            processed++;
            var key = BuildTargetKey(group.Key.Market, group.Key.Symbol);
            targetKeys.Add(key);
            var updateUnknownStocks = knownMarketsBySymbol.TryGetValue(group.Key.Symbol, out var knownMarkets)
                && knownMarkets.Count == 1
                && knownMarkets.Contains(group.Key.Market)
                && currentStocks.Any(candidate => candidate.Stock.Market == StockMarket.Unknown
                    && candidate.Symbol == group.Key.Symbol);
            var outcome = await SyncExplicitInstrumentAsync(
                group.Key.Market,
                group.Key.Symbol,
                startDate,
                endDate,
                cancellationToken,
                updateUnknownStocks);
            if (outcome.Succeeded)
            {
                succeeded++;
                successfulKeys.Add(key);
                foreach (var rowKey in outcome.AffectedRowKeys)
                    affectedRows.Add(rowKey);
            }
            else
            {
                failed++;
                failedCodes[key] = outcome.FailureCode;
                retryableFailure |= outcome.Retryable;
            }
        }

        foreach (var group in currentStocks
                     .Where(candidate => candidate.Stock.Market is StockMarket.Unknown
                         && !knownSymbols.Contains(candidate.Symbol))
                     .GroupBy(candidate => candidate.Symbol))
        {
            processed++;
            var unknownKey = BuildTargetKey(StockMarket.Unknown, group.Key);
            var outcome = await DetectAndSyncUnknownInstrumentAsync(
                group.Key,
                startDate,
                endDate,
                cancellationToken);
            if (outcome.Succeeded)
            {
                succeeded++;
                var key = outcome.ResolvedMarket.HasValue
                    ? BuildTargetKey(outcome.ResolvedMarket.Value, group.Key)
                    : unknownKey;
                targetKeys.Add(key);
                successfulKeys.Add(key);
                foreach (var rowKey in outcome.AffectedRowKeys)
                    affectedRows.Add(rowKey);
            }
            else
            {
                failed++;
                targetKeys.Add(unknownKey);
                failedCodes[unknownKey] = outcome.FailureCode;
                retryableFailure |= outcome.Retryable;
            }
        }

        var resultCode = succeeded == processed
            ? "Completed"
            : succeeded > 0
                ? "IncompleteTargets"
                : ResolveFailureCode(failedCodes.Values);
        return new HistoricalMarketDataSyncResult(
            processed,
            succeeded,
            failed,
            affectedRows.Count,
            retryableFailure,
            processed,
            targetKeys.Distinct(StringComparer.Ordinal).ToArray(),
            successfulKeys.Distinct(StringComparer.Ordinal).ToArray(),
            failedCodes,
            affectedRows.ToArray(),
            resultCode);
    }

    /// <summary>同步已由使用者指定市場的單一標的並隔離 provider 與 persistence failure。</summary>
    private async Task<InstrumentSyncOutcome> SyncExplicitInstrumentAsync(
        StockMarket market,
        string symbol,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken,
        bool updateUnknownStocks)
    {
        try
        {
            var result = await _provider.GetPricesAsync(
                market,
                symbol,
                startDate,
                endDate,
                cancellationToken);
            var affected = await PersistSuccessAsync(
                market,
                symbol,
                result,
                startDate,
                endDate,
                updateUnknownStocks,
                cancellationToken);
            return InstrumentSyncOutcome.Success(affected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HistoricalPriceProviderException exception)
        {
            _db.ChangeTracker.Clear();
            await PersistFailureAsync(
                market,
                symbol,
                MapStatus(exception.Code),
                exception.SafeMessage,
                cancellationToken);
            return InstrumentSyncOutcome.Failure(
                MapFailureCode(exception.Code),
                IsRetryableProviderCode(exception.Code));
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            _logger?.LogWarning(
                exception,
                "Historical market data synchronization failed for {Market} with safe code {ResultCode}",
                market,
                RetryClassification.IsRetryable(exception) ? "DatabaseBusy" : "DatabaseFailure");
            await PersistFailureAsync(
                market,
                symbol,
                HistoricalPriceSyncStatus.ProviderError,
                "歷史行情同步失敗",
                cancellationToken);
            return InstrumentSyncOutcome.Failure(
                RetryClassification.IsRetryable(exception) ? "DatabaseBusy" : "DatabaseFailure",
                RetryClassification.IsRetryable(exception));
        }
    }

    /// <summary>驗證未知市場的兩個候選並只在唯一成功時更新未知持股。</summary>
    private async Task<InstrumentSyncOutcome> DetectAndSyncUnknownInstrumentAsync(
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
                var affected = await PersistSuccessAsync(
                    candidate.Market,
                    symbol,
                    candidate.Result!,
                    startDate,
                    endDate,
                    true,
                    cancellationToken);
                return InstrumentSyncOutcome.Success(affected, candidate.Market);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HistoricalPriceProviderException exception)
            {
                _db.ChangeTracker.Clear();
                await PersistFailureAsync(candidate.Market, symbol, MapStatus(exception.Code), exception.SafeMessage, cancellationToken);
                return InstrumentSyncOutcome.Failure(
                    MapFailureCode(exception.Code),
                    IsRetryableProviderCode(exception.Code));
            }
            catch (Exception exception)
            {
                _db.ChangeTracker.Clear();
                _logger?.LogWarning(
                    exception,
                    "Detected market synchronization failed with safe code {ResultCode}",
                    RetryClassification.IsRetryable(exception) ? "DatabaseBusy" : "DatabaseFailure");
                await PersistFailureAsync(
                    candidate.Market,
                    symbol,
                    HistoricalPriceSyncStatus.ProviderError,
                    "歷史行情同步失敗",
                    cancellationToken);
                return InstrumentSyncOutcome.Failure(
                    RetryClassification.IsRetryable(exception) ? "DatabaseBusy" : "DatabaseFailure",
                    RetryClassification.IsRetryable(exception));
            }
        }

        if (valid.Count == 1 && uncertainCandidate)
        {
            await PersistFailureAsync(
                StockMarket.Unknown,
                symbol,
                HistoricalPriceSyncStatus.ProviderError,
                "市場辨識候選回應不完整，保留待辨識狀態",
                cancellationToken);
            var uncertainErrors = candidates
                .Where(candidate => candidate.Error is not null
                    && !IsDefinitiveCandidateFailure(candidate.Error))
                .Select(candidate => candidate.Error!);
            return InstrumentSyncOutcome.Failure(
                ResolveFailureCode(uncertainErrors.Select(error => MapFailureCode(error.Code))),
                uncertainErrors.Any(error => IsRetryableProviderCode(error.Code)));
        }

        if (valid.Count > 1)
        {
            await PersistFailureAsync(
                StockMarket.Unknown,
                symbol,
                HistoricalPriceSyncStatus.AmbiguousMarket,
                "市場辨識結果不唯一，請選擇交易市場",
                cancellationToken);
            return InstrumentSyncOutcome.Failure("AmbiguousMarket", false);
        }

        var errors = candidates
            .Where(candidate => candidate.Error is not null)
            .Select(candidate => candidate.Error!)
            .ToArray();
        var status = errors.All(error => error.Code == "no_data")
            ? HistoricalPriceSyncStatus.NoData
            : HistoricalPriceSyncStatus.ProviderError;
        await PersistFailureAsync(
            StockMarket.Unknown,
            symbol,
            status,
            status == HistoricalPriceSyncStatus.NoData ? "找不到可驗證的交易市場" : "市場辨識服務暫時無法使用",
            cancellationToken);
        return InstrumentSyncOutcome.Failure(
            status == HistoricalPriceSyncStatus.NoData
                ? "NoData"
                : ResolveFailureCode(errors.Select(error => MapFailureCode(error.Code))),
            status != HistoricalPriceSyncStatus.NoData
                && errors.Any(error => IsRetryableProviderCode(error.Code)));
    }

    /// <summary>以單一 transaction upsert 歷史價格、同步狀態及市場辨識結果。</summary>
    private async Task<IReadOnlyCollection<string>> PersistSuccessAsync(
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
        _db.ChangeTracker.Clear();

        return points
            .Select(point => $"{market}:{symbol}:{point.TradingDate:yyyy-MM-dd}")
            .ToArray();
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
        _db.ChangeTracker.Clear();
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

    /// <summary>建立尚未成功列舉目標的安全批次 failure。</summary>
    private static HistoricalMarketDataSyncResult CreateEnumerationFailure(string code, bool retryable)
        => new(
            0,
            0,
            0,
            RetryableFailure: retryable,
            TargetCount: null,
            TargetKeys: [],
            SuccessfulTargetKeys: [],
            FailedTargetCodes: new Dictionary<string, string>(StringComparer.Ordinal),
            AffectedRowKeys: [],
            ResultCode: code);

    /// <summary>將 provider 安全錯誤代碼映射到持久化同步狀態。</summary>
    private static HistoricalPriceSyncStatus MapStatus(string code)
        => code == "no_data"
            ? HistoricalPriceSyncStatus.NoData
            : code.StartsWith("invalid", StringComparison.Ordinal)
                ? HistoricalPriceSyncStatus.InvalidResponse
                : HistoricalPriceSyncStatus.ProviderError;

    /// <summary>將 provider code 映射為 runner 可查詢的 bounded failure code。</summary>
    private static string MapFailureCode(string code)
        => code == "no_data"
            ? "NoData"
            : code.StartsWith("invalid", StringComparison.Ordinal)
                ? "InvalidResponse"
                : code == "http_rejected"
                    ? "ProviderRejected"
                    : code == "unexpected_redirect"
                        ? "UnexpectedRedirect"
                        : IsRetryableProviderCode(code)
                            ? "ProviderUnavailable"
                            : "ProviderFailure";

    /// <summary>判斷 provider code 是否代表可恢復服務錯誤。</summary>
    private static bool IsRetryableProviderCode(string code)
        => code is "timeout" or "network_error" or "http_error" or "provider_error";

    /// <summary>判斷候選失敗是否足以證明該市場沒有可驗證行情。</summary>
    private static bool IsDefinitiveCandidateFailure(HistoricalPriceProviderException exception)
        => exception.Code == "no_data"
            || exception.Code.StartsWith("invalid", StringComparison.Ordinal);

    /// <summary>將多種 failure code 聚合成單一安全 result code。</summary>
    private static string ResolveFailureCode(IEnumerable<string> codes)
    {
        var distinct = codes.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 1 ? distinct[0] : "MultipleFailures";
    }

    /// <summary>建立市場與代號的 execution-local target key。</summary>
    private static string BuildTargetKey(StockMarket market, string symbol)
        => $"{market}:{symbol}";

    /// <summary>判斷目前持股是否仍屬於 execution 凍結的市場與代號。</summary>
    private static bool IsFrozenTarget(
        StockSyncCandidate candidate,
        IReadOnlySet<string> frozenKeys)
    {
        var currentKey = BuildTargetKey(candidate.Stock.Market, candidate.Symbol);
        return frozenKeys.Contains(currentKey);
    }

    /// <summary>取得目前時間在台灣市場時區的日曆日期。</summary>
    private DateOnly GetTaiwanDate()
    {
        var utc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            utc,
            BusinessScheduleCalculator.TaiwanTimeZone));
    }

    /// <summary>保存持股與正規化代號的同步候選。</summary>
    private sealed record StockSyncCandidate(Stock Stock, string Symbol);

    /// <summary>保存單一歷史標的的成功或安全失敗結果。</summary>
    private sealed record InstrumentSyncOutcome(
        bool Succeeded,
        string FailureCode,
        bool Retryable,
        IReadOnlyCollection<string> AffectedRowKeys,
        StockMarket? ResolvedMarket = null)
    {
        /// <summary>建立成功標的結果。</summary>
        public static InstrumentSyncOutcome Success(
            IReadOnlyCollection<string> affectedRowKeys,
            StockMarket? resolvedMarket = null)
            => new(true, "Completed", false, affectedRowKeys, resolvedMarket);

        /// <summary>建立安全失敗標的結果。</summary>
        public static InstrumentSyncOutcome Failure(string code, bool retryable)
            => new(false, code, retryable, []);
    }
}
