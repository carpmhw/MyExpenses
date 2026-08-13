using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

/// <summary>表示歷史同步在已知完整目標後中止，並攜帶 execution-local 部分進度。</summary>
public sealed class HistoricalMarketDataPartialFailureException : Exception
{
    /// <summary>初始化 bounded 部分失敗並保留原始中止 cause 供 runner 分類。</summary>
    public HistoricalMarketDataPartialFailureException(
        HistoricalMarketDataSyncResult partialResult,
        Exception innerException)
        : base("歷史行情同步在列舉目標後中止", innerException)
    {
        PartialResult = partialResult;
    }

    /// <summary>取得中止前已知的完整目標與已提交 row aggregate。</summary>
    public HistoricalMarketDataSyncResult PartialResult { get; }
}

/// <summary>協調目前持股、provider、歷史 upsert 與逐標的同步狀態。</summary>
public sealed class HistoricalMarketDataSynchronizer
{
    private readonly AppDbContext _db;
    private readonly IHistoricalAdjustedPriceProvider _provider;
    private readonly IOfficialMarketCatalogService _catalogService;
    private readonly ILogger<HistoricalMarketDataSynchronizer> _logger;
    private readonly TimeProvider _timeProvider;
    private IReadOnlyDictionary<string, FrozenTargetDescriptor>? _frozenTargets;
    private readonly Dictionary<int, StockMarket> _automaticallyResolvedMarkets = [];

    /// <summary>初始化使用 scoped DbContext 的歷史行情同步協調器。</summary>
    public HistoricalMarketDataSynchronizer(
        AppDbContext db,
        IHistoricalAdjustedPriceProvider provider,
        ILogger<HistoricalMarketDataSynchronizer>? logger = null,
        TimeProvider? timeProvider = null,
        IOfficialMarketCatalogService? catalogService = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? NullLogger<HistoricalMarketDataSynchronizer>.Instance;
        _catalogService = catalogService ?? new UnavailableMarketCatalogService();
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
            .Select(stock =>
            {
                var symbol = NormalizeSymbol(stock.Symbol);
                return new StockSyncCandidate(stock, symbol, BuildTargetKey(stock.Market, symbol));
            })
            .ToList();
        var frozenKeyOrder = frozenTargetKeys is null
            ? null
            : frozenTargetKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        var frozenKeys = frozenKeyOrder?.ToHashSet(StringComparer.Ordinal);
        var changedFrozenKeys = new HashSet<string>(StringComparer.Ordinal);
        if (frozenKeys is null)
        {
            _automaticallyResolvedMarkets.Clear();
            _frozenTargets = currentStocks
                .GroupBy(candidate => candidate.TargetKey)
                .ToDictionary(
                    group => group.Key,
                    group => new FrozenTargetDescriptor(
                        group.Key,
                        group.First().Stock.Market,
                        group.First().Symbol,
                        group.Select(candidate => candidate.Stock.Id).ToHashSet()),
                    StringComparer.Ordinal);
        }
        if (frozenKeys is not null)
        {
            if (_frozenTargets is not null)
            {
                var currentById = currentStocks.ToDictionary(candidate => candidate.Stock.Id);
                var frozenCandidates = new List<StockSyncCandidate>();
                foreach (var key in frozenKeyOrder!)
                {
                    if (!_frozenTargets.TryGetValue(key, out var descriptor))
                        continue;

                    var memberFound = false;
                    foreach (var stockId in descriptor.StockIds)
                    {
                        if (!currentById.TryGetValue(stockId, out var candidate)
                            || candidate.Symbol != descriptor.Symbol
                            || !MatchesFrozenTarget(descriptor, candidate))
                            continue;

                        frozenCandidates.Add(candidate with { TargetKey = descriptor.Key });
                        memberFound = true;
                    }

                    if (!memberFound)
                        changedFrozenKeys.Add(key);
                }

                currentStocks = frozenCandidates;
            }
            else
            {
                currentStocks = currentStocks
                    .Where(candidate => IsFrozenTarget(candidate, frozenKeys))
                    .ToList();
                var exactKeys = currentStocks
                    .Select(candidate => candidate.TargetKey)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var key in frozenKeys.Where(key => !exactKeys.Contains(key)))
                    changedFrozenKeys.Add(key);
            }
        }
        if (currentStocks.Count == 0)
        {
            if (changedFrozenKeys.Count > 0)
            {
                var orderedChangedKeys = frozenKeyOrder is null
                    ? changedFrozenKeys.ToArray()
                    : frozenKeyOrder.Where(changedFrozenKeys.Contains).ToArray();
                return new HistoricalMarketDataSyncResult(
                    orderedChangedKeys.Length,
                    0,
                    orderedChangedKeys.Length,
                    ResultCode: "TargetChanged",
                    TargetCount: orderedChangedKeys.Length,
                    TargetKeys: orderedChangedKeys,
                    SuccessfulTargetKeys: [],
                    FailedTargetCodes: orderedChangedKeys.ToDictionary(
                        key => key,
                        _ => "TargetChanged",
                        StringComparer.Ordinal),
                    AffectedRowKeys: []);
            }

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

        var attemptTargetKeys = changedFrozenKeys
            .Concat(currentStocks
                .Where(candidate => candidate.Stock.Market is not StockMarket.Unknown)
                .Select(candidate => candidate.TargetKey))
            .Concat(currentStocks
                .Where(candidate => candidate.Stock.Market is StockMarket.Unknown)
                .Select(candidate => BuildTargetKey(StockMarket.Unknown, candidate.Symbol)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (frozenKeyOrder is not null)
        {
            var attemptTargetSet = attemptTargetKeys.ToHashSet(StringComparer.Ordinal);
            attemptTargetKeys = frozenKeyOrder
                .Where(attemptTargetSet.Contains)
                .Concat(attemptTargetKeys.Where(key => !frozenKeys!.Contains(key)))
                .ToArray();
        }

        var successfulKeys = new List<string>();
        var failedCodes = changedFrozenKeys.ToDictionary(
            key => key,
            _ => "TargetChanged",
            StringComparer.Ordinal);
        var affectedRows = new HashSet<string>(StringComparer.Ordinal);
        var processed = changedFrozenKeys.Count;
        var succeeded = 0;
        var failed = changedFrozenKeys.Count;
        var retryableFailure = false;
        var historyCache = new Dictionary<(StockMarket Market, string Symbol), HistoricalFetch>();
        string? activeTargetKey = null;

        try
        {
            OfficialMarketCatalogSnapshot? marketCatalog = null;
            if (currentStocks.Any(candidate => candidate.Stock.Market == StockMarket.Unknown))
            {
                try
                {
                    marketCatalog = await _catalogService.FetchAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var retryable = RetryClassification.IsRetryable(exception);
                    _logger.LogWarning(
                        "Historical market catalog fetch failed with safe code {ResultCode}; retryable {Retryable}",
                        retryable ? "ProviderUnavailable" : "ProviderFailure",
                        retryable);
                    marketCatalog = CreateUnavailableCatalog(retryable);
                }
            }

            foreach (var group in currentStocks
                         .Where(candidate => candidate.Stock.Market is not StockMarket.Unknown)
                         .GroupBy(candidate => (candidate.Stock.Market, candidate.Symbol, candidate.TargetKey)))
            {
                processed++;
                activeTargetKey = group.Key.TargetKey;
                var outcome = await SyncExplicitInstrumentAsync(
                    group.Key.Market,
                    group.Key.Symbol,
                    startDate,
                    endDate,
                    cancellationToken,
                    stockIds: group.Select(candidate => candidate.Stock.Id).ToArray(),
                    failureStateMarket: group.Key.Market,
                    historyCache);
                ApplyOutcome(
                    activeTargetKey,
                    outcome,
                    successfulKeys,
                    failedCodes,
                    affectedRows,
                    ref succeeded,
                    ref failed,
                    ref retryableFailure);
                activeTargetKey = null;
            }

            foreach (var group in currentStocks
                         .Where(candidate => candidate.Stock.Market is StockMarket.Unknown)
                         .GroupBy(candidate => candidate.Symbol))
            {
                processed++;
                activeTargetKey = BuildTargetKey(StockMarket.Unknown, group.Key);
                var outcome = await SyncFromOfficialMarketCatalogAsync(
                    group.Key,
                    group.Select(candidate => candidate.Stock.Id).ToArray(),
                    marketCatalog,
                    startDate,
                    endDate,
                    cancellationToken,
                    historyCache);
                ApplyOutcome(
                    activeTargetKey,
                    outcome,
                    successfulKeys,
                    failedCodes,
                    affectedRows,
                    ref succeeded,
                    ref failed,
                    ref retryableFailure);
                activeTargetKey = null;
            }

            var resultCode = succeeded == processed
                ? "Completed"
                : succeeded > 0
                    ? "IncompleteTargets"
                    : ResolveFailureCode(failedCodes.Values);
            var successfulTargetSet = successfulKeys.ToHashSet(StringComparer.Ordinal);
            return new HistoricalMarketDataSyncResult(
                processed,
                succeeded,
                failed,
                affectedRows.Count,
                retryableFailure,
                processed,
                attemptTargetKeys,
                attemptTargetKeys.Where(successfulTargetSet.Contains).ToArray(),
                failedCodes,
                affectedRows.ToArray(),
                resultCode);
        }
        catch (HistoricalMarketDataPartialFailureException)
        {
            throw;
        }
        catch (HistoricalTargetProcessingException exception)
        {
            foreach (var rowKey in exception.AffectedRowKeys)
                affectedRows.Add(rowKey);
            var cause = exception.InnerException ?? exception;
            if (cause is HistoricalFailureStatePersistenceException persistenceException)
            {
                if (activeTargetKey is not null && !successfulKeys.Contains(activeTargetKey))
                    failedCodes[activeTargetKey] = persistenceException.FailureCode;
                throw CreatePartialFailureException(
                    persistenceException.InnerException ?? persistenceException,
                    persistenceException.FailureCode,
                    persistenceException.Retryable,
                    retryableFailure,
                    attemptTargetKeys,
                    successfulKeys,
                    failedCodes,
                    affectedRows);
            }

            var canceled = cause is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var retryable = !canceled && RetryClassification.IsRetryable(cause);
            throw CreatePartialFailureException(
                cause,
                canceled ? "Canceled" : retryable ? "DatabaseBusy" : "DatabaseFailure",
                retryable,
                retryableFailure,
                attemptTargetKeys,
                successfulKeys,
                failedCodes,
                affectedRows);
        }
        catch (HistoricalFailureStatePersistenceException exception)
        {
            if (activeTargetKey is not null && !successfulKeys.Contains(activeTargetKey))
                failedCodes[activeTargetKey] = exception.FailureCode;
            foreach (var rowKey in exception.AffectedRowKeys)
                affectedRows.Add(rowKey);
            throw CreatePartialFailureException(
                exception.InnerException ?? exception,
                exception.FailureCode,
                exception.Retryable,
                retryableFailure,
                attemptTargetKeys,
                successfulKeys,
                failedCodes,
                affectedRows);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CreatePartialFailureException(
                exception,
                "Canceled",
                false,
                retryableFailure,
                attemptTargetKeys,
                successfulKeys,
                failedCodes,
                affectedRows);
        }
        catch (Exception exception)
        {
            var retryable = RetryClassification.IsRetryable(exception);
            throw CreatePartialFailureException(
                exception,
                retryable ? "DatabaseBusy" : "DatabaseFailure",
                retryable,
                retryableFailure,
                attemptTargetKeys,
                successfulKeys,
                failedCodes,
                affectedRows);
        }
    }

    /// <summary>同步已由使用者指定市場的單一標的並隔離 provider 與 persistence failure。</summary>
    private async Task<InstrumentSyncOutcome> SyncExplicitInstrumentAsync(
        StockMarket market,
        string symbol,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? stockIds = null,
        StockMarket? failureStateMarket = null,
        IDictionary<(StockMarket Market, string Symbol), HistoricalFetch>? historyCache = null)
    {
        try
        {
            var result = await FetchHistoryAsync(
                market,
                symbol,
                startDate,
                endDate,
                historyCache,
                cancellationToken);
            var affected = await PersistSuccessAsync(
                market,
                symbol,
                result,
                startDate,
                endDate,
                stockIds,
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
            if (exception.Code != "target_changed"
                && !await HasMatchingFrozenMemberAsync(
                    stockIds,
                    market,
                    symbol,
                    cancellationToken))
                return InstrumentSyncOutcome.Failure("TargetChanged", false);
            if (exception.Code != "target_changed")
            {
                await PersistFailureAsync(
                    failureStateMarket ?? market,
                    symbol,
                    MapStatus(exception.Code),
                    exception.SafeMessage,
                    cancellationToken);
            }
            return InstrumentSyncOutcome.Failure(
                MapFailureCode(exception.Code),
                exception.Code == "target_changed"
                    ? false
                    : IsRetryableProviderCode(exception.Code));
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(
                "Historical market data synchronization failed for {Market} with safe code {ResultCode}",
                market,
                RetryClassification.IsRetryable(exception) ? "DatabaseBusy" : "DatabaseFailure");
            await PersistFailureAsync(
                failureStateMarket ?? market,
                symbol,
                HistoricalPriceSyncStatus.ProviderError,
                "歷史行情同步失敗",
                cancellationToken);
            return InstrumentSyncOutcome.Failure(
                exception is HistoricalPriceProviderException providerException
                    ? MapFailureCode(providerException.Code)
                    : RetryClassification.IsRetryable(exception) ? "DatabaseBusy" : "DatabaseFailure",
                RetryClassification.IsRetryable(exception));
        }
    }

    /// <summary>確認 provider request 後至少一筆 frozen Stock ID 仍符合實際市場與代號。</summary>
    private async Task<bool> HasMatchingFrozenMemberAsync(
        IReadOnlyCollection<int>? stockIds,
        StockMarket market,
        string symbol,
        CancellationToken cancellationToken)
    {
        if (stockIds is null)
            return true;
        return await _db.Stocks.AsNoTracking()
            .AnyAsync(
                stock => stockIds.Contains(stock.Id)
                    && stock.Market == market
                    && stock.Symbol.Trim().ToUpper() == symbol,
                cancellationToken);
    }

    /// <summary>依官方雙市場 catalog 唯一辨識後只同步對應市場的歷史行情。</summary>
    private async Task<InstrumentSyncOutcome> SyncFromOfficialMarketCatalogAsync(
        string symbol,
        IReadOnlyCollection<int> stockIds,
        OfficialMarketCatalogSnapshot? marketCatalog,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken,
        IDictionary<(StockMarket Market, string Symbol), HistoricalFetch> historyCache)
    {
        if (!await HasMatchingUnknownFrozenMemberAsync(stockIds, symbol, cancellationToken))
        {
            _db.ChangeTracker.Clear();
            return InstrumentSyncOutcome.Failure("TargetChanged", false);
        }

        if (marketCatalog is null)
        {
            await PersistFailureAsync(
                StockMarket.Unknown,
                symbol,
                HistoricalPriceSyncStatus.ProviderError,
                "官方市場清單暫時無法使用",
                cancellationToken);
            return InstrumentSyncOutcome.Failure("MarketDetectionUnavailable", true);
        }

        var resolution = OfficialMarketCatalogResolver.Resolve(marketCatalog, symbol);
        if (resolution.Market == StockMarket.Unknown)
        {
            var status = resolution.Code switch
            {
                "AmbiguousMarket" => HistoricalPriceSyncStatus.AmbiguousMarket,
                "MarketNotFound" => HistoricalPriceSyncStatus.NoData,
                _ => HistoricalPriceSyncStatus.ProviderError,
            };
            await PersistFailureAsync(
                StockMarket.Unknown,
                symbol,
                status,
                resolution.SafeMessage,
                cancellationToken);
            return InstrumentSyncOutcome.Failure(resolution.Code, resolution.Retryable);
        }

        var marketPersistence = await PersistDetectedMarketAsync(
            stockIds,
            resolution.Market,
            symbol,
            cancellationToken);
        if (!marketPersistence.Succeeded)
        {
            if (marketPersistence.Code != "TargetChanged")
            {
                await PersistFailureAsync(
                    StockMarket.Unknown,
                    symbol,
                    HistoricalPriceSyncStatus.ProviderError,
                    "歷史行情市場保存失敗",
                    cancellationToken);
            }
            return InstrumentSyncOutcome.Failure(marketPersistence.Code, marketPersistence.Retryable);
        }
        foreach (var stockId in marketPersistence.UpdatedStockIds)
            _automaticallyResolvedMarkets[stockId] = resolution.Market;
        var marketAffectedRows = marketPersistence.UpdatedStockIds
            .Select(stockId => $"stock:{stockId}")
            .ToArray();

        InstrumentSyncOutcome outcome;
        try
        {
            outcome = await SyncExplicitInstrumentAsync(
                resolution.Market,
                symbol,
                startDate,
                endDate,
                cancellationToken,
                stockIds: marketPersistence.UpdatedStockIds,
                failureStateMarket: resolution.Market,
                historyCache: historyCache);
        }
        catch (HistoricalFailureStatePersistenceException exception)
        {
            throw exception.WithAffectedRowKeys(marketAffectedRows);
        }
        catch (Exception exception)
        {
            throw new HistoricalTargetProcessingException(exception, marketAffectedRows);
        }
        return outcome with
        {
            ResolvedMarket = resolution.Market,
            AffectedRowKeys = marketAffectedRows
                .Concat(outcome.AffectedRowKeys)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
    }

    /// <summary>確認 catalog 回應後至少一筆 frozen Stock ID 仍為相同代號的 Unknown target。</summary>
    private async Task<bool> HasMatchingUnknownFrozenMemberAsync(
        IReadOnlyCollection<int> stockIds,
        string symbol,
        CancellationToken cancellationToken)
        => await _db.Stocks.AsNoTracking()
            .AnyAsync(
                stock => stockIds.Contains(stock.Id)
                    && stock.Market == StockMarket.Unknown
                    && stock.Symbol.Trim().ToUpper() == symbol,
                cancellationToken);

    /// <summary>以 frozen Stock ID 條件先保存唯一辨識市場並移除舊 Unknown 狀態。</summary>
    private async Task<MarketPersistenceResult> PersistDetectedMarketAsync(
        IReadOnlyCollection<int> stockIds,
        StockMarket market,
        string symbol,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var stocks = await _db.Stocks
                .Where(stock => stockIds.Contains(stock.Id))
                .ToListAsync(cancellationToken);
            var eligible = stocks
                .Where(stock => stock.Market == StockMarket.Unknown
                    && NormalizeSymbol(stock.Symbol) == symbol)
                .ToList();
            if (eligible.Count == 0)
            {
                _db.ChangeTracker.Clear();
                return MarketPersistenceResult.Failure("TargetChanged", false);
            }

            foreach (var stock in eligible)
                stock.Market = market;

            var unknownState = await _db.HistoricalPriceSyncStates
                .SingleOrDefaultAsync(
                    state => state.Market == StockMarket.Unknown && state.Symbol == symbol,
                    cancellationToken);
            if (unknownState is not null)
                _db.HistoricalPriceSyncStates.Remove(unknownState);

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return MarketPersistenceResult.Success(eligible.Select(stock => stock.Id).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            var retryable = RetryClassification.IsRetryable(exception);
            return MarketPersistenceResult.Failure(
                retryable ? "DatabaseBusy" : "DatabaseFailure",
                retryable);
        }
    }

    /// <summary>在單次歷史同步 attempt 內快取每個市場代號的 provider 結果。</summary>
    private async Task<HistoricalPriceProviderResult> FetchHistoryAsync(
        StockMarket market,
        string symbol,
        DateOnly startDate,
        DateOnly endDate,
        IDictionary<(StockMarket Market, string Symbol), HistoricalFetch>? historyCache,
        CancellationToken cancellationToken)
    {
        var key = (market, symbol);
        if (historyCache?.TryGetValue(key, out var cached) == true)
        {
            if (cached.Failure is not null)
                throw cached.Failure;
            return cached.Result!;
        }

        try
        {
            var result = await _provider.GetPricesAsync(
                market,
                symbol,
                startDate,
                endDate,
                cancellationToken);
            historyCache?[key] = new(result, null);
            return result;
        }
        catch (HistoricalPriceProviderException exception)
        {
            historyCache?[key] = new(null, exception);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var failure = new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
            historyCache?[key] = new(null, failure);
            throw failure;
        }
        catch (Exception exception)
        {
            var failure = new HistoricalPriceProviderException(
                RetryClassification.IsRetryable(exception) ? "provider_error" : "provider_failure",
                "歷史行情同步失敗");
            historyCache?[key] = new(null, failure);
            throw failure;
        }
    }

    /// <summary>以單一 transaction upsert 歷史價格、同步狀態及市場辨識結果。</summary>
    private async Task<IReadOnlyCollection<string>> PersistSuccessAsync(
        StockMarket market,
        string symbol,
        HistoricalPriceProviderResult result,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyCollection<int>? stockIds,
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
        if (stockIds is not null)
        {
            var targetStocks = await _db.Stocks
                .Where(stock => stockIds.Contains(stock.Id))
                .ToListAsync(cancellationToken);
            if (!targetStocks.Any(stock => stock.Market == market
                    && NormalizeSymbol(stock.Symbol) == symbol))
                throw new HistoricalPriceProviderException("target_changed", "持股目標已變更");
        }

        var existing = await _db.HistoricalAdjustedPrices
            .Where(price => price.Market == market && price.Symbol == symbol)
            .ToListAsync(cancellationToken);
        var existingByDate = existing.ToDictionary(price => price.TradingDate);
        var fetchedAt = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
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
        try
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            var retryable = RetryClassification.IsRetryable(exception);
            throw new HistoricalFailureStatePersistenceException(
                retryable ? "DatabaseBusy" : "DatabaseFailure",
                retryable,
                exception);
        }
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

    /// <summary>將單一 target outcome 合併到 attempt-local disposition 與 affected rows。</summary>
    private static void ApplyOutcome(
        string targetKey,
        InstrumentSyncOutcome outcome,
        ICollection<string> successfulKeys,
        IDictionary<string, string> failedCodes,
        ISet<string> affectedRows,
        ref int succeeded,
        ref int failed,
        ref bool retryableFailure)
    {
        if (outcome.Succeeded)
        {
            succeeded++;
            successfulKeys.Add(targetKey);
            failedCodes.Remove(targetKey);
        }
        else
        {
            failed++;
            failedCodes[targetKey] = outcome.FailureCode;
            retryableFailure |= outcome.Retryable;
        }

        foreach (var rowKey in outcome.AffectedRowKeys)
            affectedRows.Add(rowKey);
    }

    /// <summary>以 bounded cause 填滿 pending targets 並建立完整 attempt universe 的 partial failure。</summary>
    private static HistoricalMarketDataPartialFailureException CreatePartialFailureException(
        Exception cause,
        string causeCode,
        bool causeRetryable,
        bool previousRetryableFailure,
        IReadOnlyCollection<string> targetKeys,
        IReadOnlyCollection<string> successfulKeys,
        IReadOnlyDictionary<string, string> failedCodes,
        IReadOnlyCollection<string> affectedRows)
    {
        var successfulSet = successfulKeys.ToHashSet(StringComparer.Ordinal);
        var finalFailedCodes = new Dictionary<string, string>(failedCodes, StringComparer.Ordinal);
        foreach (var targetKey in targetKeys)
        {
            if (!successfulSet.Contains(targetKey) && !finalFailedCodes.ContainsKey(targetKey))
                finalFailedCodes[targetKey] = causeCode;
        }

        var succeeded = targetKeys.Count(successfulSet.Contains);
        var failed = targetKeys.Count - succeeded;
        var resultCode = succeeded > 0
            ? "IncompleteTargets"
            : ResolveFailureCode(finalFailedCodes.Values);
        var partialResult = new HistoricalMarketDataSyncResult(
            targetKeys.Count,
            succeeded,
            failed,
            affectedRows.Count,
            previousRetryableFailure || causeRetryable,
            targetKeys.Count,
            targetKeys.ToArray(),
            targetKeys.Where(successfulSet.Contains).ToArray(),
            finalFailedCodes,
            affectedRows.ToArray(),
            resultCode);
        return new HistoricalMarketDataPartialFailureException(partialResult, cause);
    }

    /// <summary>將 provider 安全錯誤代碼映射到持久化同步狀態。</summary>
    private static HistoricalPriceSyncStatus MapStatus(string code)
        => code == "no_data"
            ? HistoricalPriceSyncStatus.NoData
            : code.StartsWith("invalid", StringComparison.Ordinal)
                ? HistoricalPriceSyncStatus.InvalidResponse
                : HistoricalPriceSyncStatus.ProviderError;

    /// <summary>將 provider code 映射為 runner 可查詢的 bounded failure code。</summary>
    private static string MapFailureCode(string code)
        => code == "target_changed"
            ? "TargetChanged"
            : code == "provider_failure"
                ? "ProviderFailure"
            : code == "no_data"
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

    /// <summary>將多種 failure code 聚合成單一安全 result code。</summary>
    private static string ResolveFailureCode(IEnumerable<string> codes)
    {
        var distinct = codes.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 1 ? distinct[0] : "MultipleFailures";
    }

    /// <summary>建立官方 catalog 未注入時的安全失敗快照，禁止回退 Yahoo 市場猜測。</summary>
    private sealed class UnavailableMarketCatalogService : IOfficialMarketCatalogService
    {
        /// <summary>回傳雙來源皆不可用的 bounded catalog 結果。</summary>
        public Task<OfficialMarketCatalogSnapshot> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateUnavailableCatalog(true));

        /// <summary>回傳不可用的市場辨識結果。</summary>
        public Task<OfficialMarketResolution> LookupAsync(
            string? symbol,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OfficialMarketResolution(
                StockMarket.Unknown,
                "MarketDetectionUnavailable",
                Retryable: true,
                SafeMessage: "官方市場清單暫時無法使用"));
    }

    /// <summary>建立雙來源皆失敗的安全官方 catalog 快照。</summary>
    private static OfficialMarketCatalogSnapshot CreateUnavailableCatalog(bool retryable)
        => new(
            CurrentPriceProviderResult.Failed(
                "TWSE",
                retryable ? "ProviderUnavailable" : "ProviderFailure",
                "官方市場清單暫時無法使用",
                retryable,
                "twse-current-price"),
            CurrentPriceProviderResult.Failed(
                "TPEx",
                retryable ? "ProviderUnavailable" : "ProviderFailure",
                "官方市場清單暫時無法使用",
                retryable,
                "tpex-current-price"));

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

    /// <summary>依 frozen Stock ID 與原始 identity 判斷目前成員是否仍屬於原 target。</summary>
    private bool MatchesFrozenTarget(
        FrozenTargetDescriptor descriptor,
        StockSyncCandidate candidate)
    {
        if (candidate.Stock.Market == descriptor.Market)
            return true;
        return descriptor.Market == StockMarket.Unknown
            && _automaticallyResolvedMarkets.TryGetValue(candidate.Stock.Id, out var resolvedMarket)
            && candidate.Stock.Market == resolvedMarket;
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
    private sealed record StockSyncCandidate(Stock Stock, string Symbol, string TargetKey);

    /// <summary>保存第一次 attempt 的原始 target identity 與 frozen Stock ID 成員。</summary>
    private sealed record FrozenTargetDescriptor(
        string Key,
        StockMarket Market,
        string Symbol,
        IReadOnlySet<int> StockIds);

    /// <summary>保存單次歷史 provider 請求的成功或安全失敗結果。</summary>
    private sealed record HistoricalFetch(
        HistoricalPriceProviderResult? Result,
        HistoricalPriceProviderException? Failure);

    /// <summary>保存單一 target 深層中止 cause 與中止前已提交的 row keys。</summary>
    private sealed class HistoricalTargetProcessingException : Exception
    {
        /// <summary>初始化深層 target 中止 marker。</summary>
        public HistoricalTargetProcessingException(
            Exception innerException,
            IReadOnlyCollection<string> affectedRowKeys)
            : base("歷史行情 target 處理於提交部分資料後中止", innerException)
        {
            AffectedRowKeys = affectedRowKeys;
        }

        /// <summary>取得中止前已提交且必須保留的 row keys。</summary>
        public IReadOnlyCollection<string> AffectedRowKeys { get; }
    }

    /// <summary>保存 failure-state persistence 的 bounded 分類與中止前已提交 row keys。</summary>
    private sealed class HistoricalFailureStatePersistenceException : Exception
    {
        /// <summary>初始化 failure-state persistence 錯誤分類。</summary>
        public HistoricalFailureStatePersistenceException(
            string failureCode,
            bool retryable,
            Exception innerException,
            IReadOnlyCollection<string>? affectedRowKeys = null)
            : base("歷史行情失敗狀態保存失敗", innerException)
        {
            FailureCode = failureCode;
            Retryable = retryable;
            AffectedRowKeys = affectedRowKeys ?? [];
        }

        /// <summary>取得 runner 使用的 bounded failure code。</summary>
        public string FailureCode { get; }

        /// <summary>取得原始 persistence 例外是否可重試。</summary>
        public bool Retryable { get; }

        /// <summary>取得中止前已提交且不可遺失的 row keys。</summary>
        public IReadOnlyCollection<string> AffectedRowKeys { get; }

        /// <summary>建立加入額外已提交 row keys 的同源 persistence 例外。</summary>
        public HistoricalFailureStatePersistenceException WithAffectedRowKeys(
            IReadOnlyCollection<string> affectedRowKeys)
            => new(
                FailureCode,
                Retryable,
                InnerException ?? this,
                AffectedRowKeys
                    .Concat(affectedRowKeys)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
    }

    /// <summary>保存市場辨識 transaction 的安全結果。</summary>
    private sealed record MarketPersistenceResult(
        bool Succeeded,
        string Code,
        bool Retryable,
        IReadOnlyCollection<int>? UpdatedStockIds = null)
    {
        /// <summary>取得市場 transaction 實際更新的 frozen Stock ID。</summary>
        public IReadOnlyCollection<int> UpdatedStockIds { get; } = UpdatedStockIds ?? [];

        /// <summary>建立成功的市場保存結果。</summary>
        public static MarketPersistenceResult Success(IReadOnlyCollection<int> updatedStockIds)
            => new(true, "Completed", false, updatedStockIds);

        /// <summary>建立市場保存失敗結果。</summary>
        public static MarketPersistenceResult Failure(string code, bool retryable)
            => new(false, code, retryable);
    }

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
