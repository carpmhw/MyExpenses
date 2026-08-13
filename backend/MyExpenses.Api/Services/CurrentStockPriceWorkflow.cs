using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>表示目前價格同步在列舉完整目標後中止，並攜帶 execution-local 部分進度。</summary>
public sealed class CurrentStockPricePartialFailureException : Exception
{
    /// <summary>初始化部分失敗並保留原始中止 cause 供 runner 分類。</summary>
    public CurrentStockPricePartialFailureException(
        ScheduledJobWorkflowResult partialResult,
        Exception innerException)
        : base("目前價格同步在列舉目標後中止", innerException)
    {
        PartialResult = partialResult;
    }

    /// <summary>取得中止前已知的完整目標與已提交 row aggregate。</summary>
    public ScheduledJobWorkflowResult PartialResult { get; }
}

/// <summary>依持股市場分流 provider 並以短 transaction 保存目前價格的 workflow。</summary>
public sealed class CurrentStockPriceWorkflow
{
    private readonly AppDbContext _db;
    private readonly ICurrentPriceProvider _twseProvider;
    private readonly ICurrentPriceProvider _tpexProvider;
    private readonly IOfficialMarketCatalogService? _catalogService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CurrentStockPriceWorkflow>? _logger;
    private IReadOnlyDictionary<string, FrozenStockPriceTarget>? _frozenTargets;
    private readonly Dictionary<int, StockMarket> _automaticallyResolvedMarkets = [];

    /// <summary>初始化目前價格 workflow 與兩個市場 provider。</summary>
    public CurrentStockPriceWorkflow(
        AppDbContext db,
        ICurrentPriceProvider twseProvider,
        ICurrentPriceProvider tpexProvider,
        TimeProvider? timeProvider = null,
        ILogger<CurrentStockPriceWorkflow>? logger = null,
        IOfficialMarketCatalogService? catalogService = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _twseProvider = twseProvider ?? throw new ArgumentNullException(nameof(twseProvider));
        _tpexProvider = tpexProvider ?? throw new ArgumentNullException(nameof(tpexProvider));
        _catalogService = catalogService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>列舉目前持股、分流兩個市場並回傳 execution 可聚合的結果。</summary>
    public async Task<ScheduledJobWorkflowResult> RunAsync(
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? frozenTargetKeys = null)
    {
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

        var currentTargets = stocks
            .Select(stock => new StockPriceTarget(
                stock.Id.ToString(CultureInfo.InvariantCulture),
                stock.Id,
                NormalizeSymbol(stock.Symbol),
                stock.Market))
            .ToList();
        List<StockPriceTarget> targets;
        string[] targetKeys;
        var changedTargetKeys = new HashSet<string>(StringComparer.Ordinal);
        if (frozenTargetKeys is null)
        {
            _automaticallyResolvedMarkets.Clear();
            _frozenTargets = currentTargets.ToDictionary(
                target => target.Key,
                target => new FrozenStockPriceTarget(target.Id, target.Symbol, target.Market),
                StringComparer.Ordinal);
            targets = currentTargets;
            targetKeys = targets.Select(target => target.Key).ToArray();
        }
        else
        {
            targetKeys = frozenTargetKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var currentByKey = currentTargets.ToDictionary(target => target.Key, StringComparer.Ordinal);
            targets = [];
            foreach (var key in targetKeys)
            {
                if (!currentByKey.TryGetValue(key, out var current))
                {
                    changedTargetKeys.Add(key);
                    continue;
                }

                if (_frozenTargets is null || !_frozenTargets.TryGetValue(key, out var descriptor))
                {
                    targets.Add(current);
                    continue;
                }

                if (MatchesFrozenTarget(descriptor, current))
                    targets.Add(current);
                else
                    changedTargetKeys.Add(key);
            }
        }
        if (targetKeys.Length == 0)
            return ScheduledJobWorkflowResult.NoWork("NoEligibleTargets");

        var succeeded = new List<string>();
        var failed = changedTargetKeys.ToDictionary(
            key => key,
            _ => "TargetChanged",
            StringComparer.Ordinal);
        var affected = new List<string>();
        var retryableFailure = false;
        var providerRecordCount = 0;
        var unmatchedCount = 0;
        var invalidCount = 0;
        try
        {
            OfficialMarketCatalogSnapshot? catalogSnapshot = null;
            string? catalogFailureCode = null;
            foreach (var target in targets.Where(target => target.Symbol.Length == 0))
                failed[target.Key] = "InvalidSymbol";

            var unknownTargets = targets
                .Where(target => target.Symbol.Length > 0 && target.Market == StockMarket.Unknown)
                .ToList();
            if (unknownTargets.Count > 0 && _catalogService is not null)
            {
                catalogSnapshot = await _catalogService.FetchAsync(cancellationToken);
            }

            if (catalogSnapshot is not null)
            {
            providerRecordCount = catalogSnapshot.Twse.Records.Count
                + catalogSnapshot.Tpex.Records.Count;
            invalidCount = catalogSnapshot.Twse.Records.Count(record =>
                    !record.Price.HasValue || record.Price.Value <= 0m)
                + catalogSnapshot.Tpex.Records.Count(record =>
                    !record.Price.HasValue || record.Price.Value <= 0m);
            var twseSymbols = targets
                .Where(target => target.Market == StockMarket.Twse || target.Market == StockMarket.Unknown)
                .Select(target => target.Symbol)
                .ToHashSet(StringComparer.Ordinal);
            var tpexSymbols = targets
                .Where(target => target.Market == StockMarket.Tpex || target.Market == StockMarket.Unknown)
                .Select(target => target.Symbol)
                .ToHashSet(StringComparer.Ordinal);
            unmatchedCount = catalogSnapshot.Twse.Records.Count(record =>
                    !twseSymbols.Contains(NormalizeSymbol(record.Symbol)))
                + catalogSnapshot.Tpex.Records.Count(record =>
                    !tpexSymbols.Contains(NormalizeSymbol(record.Symbol)));
            }

            foreach (var group in targets
                     .Where(target => target.Symbol.Length > 0 && target.Market is StockMarket.Twse or StockMarket.Tpex)
                     .GroupBy(target => target.Market))
        {
            var provider = group.Key == StockMarket.Twse ? _twseProvider : _tpexProvider;
            var providerResult = catalogSnapshot is not null
                ? group.Key == StockMarket.Twse
                    ? catalogSnapshot.Twse
                    : catalogSnapshot.Tpex
                : await provider.FetchAsync(cancellationToken);
            var failure = providerResult.Failure;
            if (failure is null && providerResult.Records.Count == 0)
            {
                failure = new CurrentPriceProviderFailure(
                    "ProviderUnavailable",
                    "行情服務沒有回傳資料",
                    true,
                    group.Key == StockMarket.Twse ? "twse-current-price" : "tpex-current-price");
            }

            if (failure is not null)
            {
                _logger?.LogWarning(
                    "Current price provider failure from {Provider} at {LogicalEndpoint} with code {ResultCode}; "
                    + "target count {TargetCount}, provider record count {ProviderRecordCount}, "
                    + "updated count {UpdatedCount}, unmatched count {UnmatchedCount}, "
                    + "invalid count {InvalidCount}, failed count {FailedCount}",
                    providerResult.Provider,
                    failure.LogicalEndpoint,
                    failure.Code,
                    targets.Count,
                    providerRecordCount,
                    succeeded.Count,
                    unmatchedCount,
                    invalidCount,
                    failed.Count);
                var validTargetIds = await RevalidateFailureTargetsAsync(group, cancellationToken);
                retryableFailure |= failure.Retryable && validTargetIds.Count > 0;
                foreach (var target in group)
                    failed[target.Key] = validTargetIds.Contains(target.Id)
                        ? failure.Code
                        : "TargetChanged";
                continue;
            }

            if (catalogSnapshot is null)
                providerRecordCount += providerResult.Records.Count;
            var targetSymbols = group
                .Select(target => target.Symbol)
                .ToHashSet(StringComparer.Ordinal);
            if (catalogSnapshot is null)
            {
                unmatchedCount += providerResult.Records.Count(record =>
                    !targetSymbols.Contains(NormalizeSymbol(record.Symbol)));
                invalidCount += providerResult.Records.Count(record =>
                    !record.Price.HasValue || record.Price.Value <= 0m);
            }
            var records = providerResult.Records
                .GroupBy(record => NormalizeSymbol(record.Symbol))
                .ToDictionary(grouping => grouping.Key, grouping => grouping.Last(), StringComparer.Ordinal);
            foreach (var symbolGroup in group.GroupBy(target => target.Symbol))
            {
                var symbolTargets = symbolGroup.ToArray();
                var validTargetIds = await RevalidateFailureTargetsAsync(symbolTargets, cancellationToken);
                foreach (var target in symbolTargets.Where(target => !validTargetIds.Contains(target.Id)))
                    failed[target.Key] = "TargetChanged";
                var validTargets = symbolTargets
                    .Where(target => validTargetIds.Contains(target.Id))
                    .ToArray();

                if (!records.TryGetValue(symbolGroup.Key, out var record))
                {
                    foreach (var target in validTargets)
                        failed[target.Key] = "NoMatchingPrice";
                    continue;
                }

                if (!record.Price.HasValue || record.Price.Value <= 0m)
                {
                    foreach (var target in validTargets)
                        failed[target.Key] = "InvalidPrice";
                    continue;
                }

                var targetIds = validTargets.Select(target => target.Id).ToArray();
                var persistence = await PersistPriceAsync(
                    targetIds,
                    group.Key,
                    symbolGroup.Key,
                    record.Price.Value,
                    cancellationToken);
                if (!persistence.Succeeded)
                {
                    retryableFailure |= persistence.Retryable;
                    foreach (var target in validTargets)
                        failed[target.Key] = persistence.Code;
                    continue;
                }

                foreach (var target in validTargets)
                {
                    if (!persistence.UpdatedStockIds.Contains(target.Id))
                    {
                        failed[target.Key] = "TargetChanged";
                        continue;
                    }

                    succeeded.Add(target.Key);
                    affected.Add(target.Key);
                }
            }
            }

            foreach (var symbolGroup in unknownTargets.GroupBy(target => target.Symbol))
            {
            if (_catalogService is null)
            {
                foreach (var target in symbolGroup)
                    failed[target.Key] = "UnknownMarket";
                continue;
            }

            if (catalogSnapshot is null)
            {
                var validTargetIds = await RevalidateFailureTargetsAsync(symbolGroup, cancellationToken);
                retryableFailure |= catalogFailureCode == "NetworkError" && validTargetIds.Count > 0;
                foreach (var target in symbolGroup)
                    failed[target.Key] = validTargetIds.Contains(target.Id)
                        ? catalogFailureCode ?? "MarketDetectionUnavailable"
                        : "TargetChanged";
                continue;
            }

            var resolution = OfficialMarketCatalogResolver.Resolve(catalogSnapshot, symbolGroup.Key);
            if (resolution.Market == StockMarket.Unknown)
            {
                var validTargetIds = await RevalidateFailureTargetsAsync(symbolGroup, cancellationToken);
                retryableFailure |= resolution.Retryable && validTargetIds.Count > 0;
                foreach (var target in symbolGroup)
                    failed[target.Key] = validTargetIds.Contains(target.Id)
                        ? resolution.Code
                        : "TargetChanged";
                continue;
            }

            var record = resolution.Record!;
            var targetIds = symbolGroup.Select(target => target.Id).ToArray();
            var persistence = record.Price is > 0m
                ? await PersistResolvedPriceAsync(
                    targetIds,
                    resolution.Market,
                    symbolGroup.Key,
                    record.Price.Value,
                    cancellationToken)
                : await PersistResolvedMarketAsync(
                    targetIds,
                    resolution.Market,
                    symbolGroup.Key,
                    cancellationToken);
            if (!persistence.Succeeded)
            {
                retryableFailure |= persistence.Retryable;
                foreach (var target in symbolGroup)
                    failed[target.Key] = persistence.Code;
                continue;
            }
            foreach (var stockId in persistence.UpdatedStockIds)
                _automaticallyResolvedMarkets[stockId] = resolution.Market;

            foreach (var target in symbolGroup)
            {
                if (!persistence.UpdatedStockIds.Contains(target.Id))
                {
                    failed[target.Key] = "TargetChanged";
                    continue;
                }

                affected.Add(target.Key);
                if (record.Price is > 0m)
                    succeeded.Add(target.Key);
                else
                    failed[target.Key] = "InvalidPrice";
            }
            }

            var succeededKeys = succeeded.Distinct(StringComparer.Ordinal).ToArray();
            var failedKeys = failed
            .Where(pair => !succeededKeys.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var resultCode = ResolveResultCode(targetKeys.Length, succeededKeys.Length, failedKeys.Values);
            var result = new ScheduledJobWorkflowResult
            {
            Outcome = succeededKeys.Length == targetKeys.Length
                ? ScheduledJobWorkflowOutcome.Succeeded
                : succeededKeys.Length > 0
                    ? ScheduledJobWorkflowOutcome.PartiallySucceeded
                    : ScheduledJobWorkflowOutcome.Failed,
            Retryability = retryableFailure
                ? ScheduledJobRetryClassification.Retryable
                : ScheduledJobRetryClassification.Permanent,
            TargetsEnumerated = true,
            TargetCount = targetKeys.Length,
            SucceededCount = succeededKeys.Length,
            FailedCount = failedKeys.Count,
            AffectedCount = affected.Distinct(StringComparer.Ordinal).Count(),
            ProviderRecordCount = providerRecordCount,
            UpdatedCount = succeededKeys.Length,
            UnmatchedCount = unmatchedCount,
            InvalidCount = invalidCount,
            TargetKeys = targetKeys,
            SucceededTargetKeys = succeededKeys,
            FailedTargetCodes = failedKeys,
            AffectedRowKeys = affected.Distinct(StringComparer.Ordinal).ToArray(),
            ResultCode = resultCode,
            SafeMessage = "目前價格批次已完成 aggregate 處理",
            };

            _logger?.LogInformation(
            "Current price execution aggregate completed with result code {ResultCode}; "
            + "target count {TargetCount}, succeeded count {SucceededCount}, failed count {FailedCount}, "
            + "affected count {AffectedCount}, provider record count {ProviderRecordCount}, "
            + "updated count {UpdatedCount}, unmatched count {UnmatchedCount}, invalid count {InvalidCount}",
            resultCode,
            targetKeys.Length,
            succeededKeys.Length,
            failedKeys.Count,
            affected.Distinct(StringComparer.Ordinal).Count(),
            providerRecordCount,
            succeededKeys.Length,
            unmatchedCount,
                invalidCount);

            return result;
        }
        catch (CurrentStockPricePartialFailureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var canceled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var retryable = !canceled && (exception is OperationCanceledException || RetryClassification.IsRetryable(exception));
            throw CreatePartialFailureException(
                exception,
                canceled ? "Canceled" : retryable ? "TransientFailure" : "DatabaseFailure",
                retryable,
                targetKeys,
                succeeded,
                failed,
                affected,
                retryableFailure,
                providerRecordCount,
                unmatchedCount,
                invalidCount);
        }
    }

    /// <summary>以短 transaction 更新一組相同價格的持股並回傳 DB failure 分類。</summary>
    private async Task<PricePersistenceResult> PersistPriceAsync(
        IReadOnlyCollection<int> stockIds,
        StockMarket expectedMarket,
        string expectedSymbol,
        decimal price,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var stocks = await _db.Stocks
                .Where(stock => stockIds.Contains(stock.Id))
                .ToListAsync(cancellationToken);
            var eligible = stocks
                .Where(stock => stock.Market == expectedMarket
                    && NormalizeSymbol(stock.Symbol) == expectedSymbol)
                .ToList();

            var nowUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
            foreach (var stock in eligible)
            {
                stock.CurrentPrice = price;
                stock.LastPriceUpdate = nowUtc;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return new PricePersistenceResult(
                true,
                false,
                "Completed",
                eligible.Select(stock => stock.Id).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            var retryable = RetryClassification.IsRetryable(exception);
            return new PricePersistenceResult(
                false,
                retryable,
                retryable ? "DatabaseBusy" : "DatabaseFailure");
        }
    }

    /// <summary>以市場尚未辨識為條件，原子更新一組持股的市場與有效目前價格。</summary>
    private async Task<PricePersistenceResult> PersistResolvedPriceAsync(
        IReadOnlyCollection<int> stockIds,
        StockMarket expectedMarket,
        string expectedSymbol,
        decimal price,
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
                    && NormalizeSymbol(stock.Symbol) == expectedSymbol)
                .ToList();

            var nowUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
            foreach (var stock in eligible)
            {
                stock.Market = expectedMarket;
                stock.CurrentPrice = price;
                stock.LastPriceUpdate = nowUtc;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return new PricePersistenceResult(true, false, "Completed", eligible.Select(stock => stock.Id).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            var retryable = RetryClassification.IsRetryable(exception);
            return new PricePersistenceResult(
                false,
                retryable,
                retryable ? "DatabaseBusy" : "DatabaseFailure");
        }
    }

    /// <summary>以市場尚未辨識為條件，原子更新一組持股的市場而不保存無效價格。</summary>
    private async Task<PricePersistenceResult> PersistResolvedMarketAsync(
        IReadOnlyCollection<int> stockIds,
        StockMarket expectedMarket,
        string expectedSymbol,
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
                    && NormalizeSymbol(stock.Symbol) == expectedSymbol)
                .ToList();

            foreach (var stock in eligible)
                stock.Market = expectedMarket;

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return new PricePersistenceResult(true, false, "Completed", eligible.Select(stock => stock.Id).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            var retryable = RetryClassification.IsRetryable(exception);
            return new PricePersistenceResult(
                false,
                retryable,
                retryable ? "DatabaseBusy" : "DatabaseFailure");
        }
    }

    /// <summary>建立尚未成功列舉目標的安全 failure result。</summary>
    private static ScheduledJobWorkflowResult CreateEnumerationFailure(string code, bool retryable)
        => new()
        {
            Outcome = ScheduledJobWorkflowOutcome.Failed,
            Retryability = retryable
                ? ScheduledJobRetryClassification.Retryable
                : ScheduledJobRetryClassification.Permanent,
            TargetsEnumerated = false,
            ResultCode = code,
            FailureCodes = [code],
            SafeMessage = "目前持股目標列舉失敗",
        };

    /// <summary>以中止 cause 填滿尚未成功目標並建立 execution-local 部分結果。</summary>
    private static CurrentStockPricePartialFailureException CreatePartialFailureException(
        Exception cause,
        string causeCode,
        bool causeRetryable,
        IReadOnlyCollection<string> targetKeys,
        IReadOnlyCollection<string> succeeded,
        IReadOnlyDictionary<string, string> failed,
        IReadOnlyCollection<string> affected,
        bool previousRetryableFailure,
        int providerRecordCount,
        int unmatchedCount,
        int invalidCount)
    {
        var succeededKeys = succeeded
            .Where(targetKeys.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var succeededSet = succeededKeys.ToHashSet(StringComparer.Ordinal);
        var failedCodes = new Dictionary<string, string>(failed, StringComparer.Ordinal);
        foreach (var targetKey in targetKeys)
        {
            if (!succeededSet.Contains(targetKey) && !failedCodes.ContainsKey(targetKey))
                failedCodes[targetKey] = causeCode;
        }

        var failedKeys = failedCodes
            .Where(pair => !succeededSet.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var resultCode = ResolveResultCode(targetKeys.Count, succeededKeys.Length, failedKeys.Values);
        var partialResult = new ScheduledJobWorkflowResult
        {
            Outcome = succeededKeys.Length == targetKeys.Count
                ? ScheduledJobWorkflowOutcome.Succeeded
                : succeededKeys.Length > 0
                    ? ScheduledJobWorkflowOutcome.PartiallySucceeded
                    : ScheduledJobWorkflowOutcome.Failed,
            Retryability = previousRetryableFailure || causeRetryable
                ? ScheduledJobRetryClassification.Retryable
                : ScheduledJobRetryClassification.Permanent,
            TargetsEnumerated = true,
            TargetCount = targetKeys.Count,
            SucceededCount = succeededKeys.Length,
            FailedCount = failedKeys.Count,
            AffectedCount = affected.Distinct(StringComparer.Ordinal).Count(),
            ProviderRecordCount = providerRecordCount,
            UpdatedCount = succeededKeys.Length,
            UnmatchedCount = unmatchedCount,
            InvalidCount = invalidCount,
            TargetKeys = targetKeys.ToArray(),
            SucceededTargetKeys = succeededKeys,
            FailedTargetCodes = failedKeys,
            AffectedRowKeys = affected.Distinct(StringComparer.Ordinal).ToArray(),
            ResultCode = resultCode,
            SafeMessage = "目前價格批次在 aggregate 處理中止",
        };
        return new CurrentStockPricePartialFailureException(partialResult, cause);
    }

    /// <summary>依唯一目標 disposition 推導目前價格 batch 的安全 result code。</summary>
    private static string ResolveResultCode(
        int targetCount,
        int succeededCount,
        IEnumerable<string> failureCodes)
    {
        if (targetCount == 0)
            return "NoEligibleTargets";
        if (succeededCount == targetCount)
            return "Completed";
        if (succeededCount > 0)
            return "IncompleteTargets";
        var distinctCodes = failureCodes.Distinct(StringComparer.Ordinal).ToArray();
        return distinctCodes.Length == 1 ? distinctCodes[0] : "MultipleFailures";
    }

    /// <summary>正規化持股代號供 provider records 比對。</summary>
    private static string NormalizeSymbol(string? symbol)
        => symbol?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>依首次列舉 identity 與 execution-local 自動市場轉換驗證 frozen target。</summary>
    private bool MatchesFrozenTarget(FrozenStockPriceTarget descriptor, StockPriceTarget current)
    {
        if (current.Symbol != descriptor.Symbol)
            return false;
        if (current.Market == descriptor.Market)
            return true;
        return descriptor.Market == StockMarket.Unknown
            && _automaticallyResolvedMarkets.TryGetValue(current.Id, out var resolvedMarket)
            && current.Market == resolvedMarket;
    }

    /// <summary>在 provider failure 後重新確認 frozen Stock ID 仍符合原始 identity。</summary>
    private async Task<IReadOnlySet<int>> RevalidateFailureTargetsAsync(
        IEnumerable<StockPriceTarget> targets,
        CancellationToken cancellationToken)
    {
        var expectedTargets = targets.ToDictionary(target => target.Id);
        var stockIds = expectedTargets.Keys.ToArray();
        var currentTargets = await _db.Stocks.AsNoTracking()
            .Where(stock => stockIds.Contains(stock.Id))
            .Select(stock => new StockPriceTarget(
                stock.Id.ToString(CultureInfo.InvariantCulture),
                stock.Id,
                NormalizeSymbol(stock.Symbol),
                stock.Market))
            .ToListAsync(cancellationToken);
        var validIds = new HashSet<int>();
        foreach (var current in currentTargets)
        {
            var expected = expectedTargets[current.Id];
            var descriptor = _frozenTargets is not null
                && _frozenTargets.TryGetValue(expected.Key, out var frozen)
                    ? frozen
                    : new FrozenStockPriceTarget(expected.Id, expected.Symbol, expected.Market);
            if (MatchesFrozenTarget(descriptor, current))
                validIds.Add(current.Id);
        }

        return validIds;
    }

    /// <summary>保存 execution-local 目前價格目標欄位。</summary>
    private sealed record StockPriceTarget(string Key, int Id, string Symbol, StockMarket Market);

    /// <summary>保存第一次 attempt 凍結的 Stock ID、正規化代號與市場。</summary>
    private sealed record FrozenStockPriceTarget(int Id, string Symbol, StockMarket Market);

    /// <summary>保存單次持股價格 transaction 的安全結果。</summary>
    private sealed record PricePersistenceResult(
        bool Succeeded,
        bool Retryable,
        string Code,
        IReadOnlyCollection<int>? UpdatedStockIds = null)
    {
        /// <summary>取得本次 transaction 實際更新的持股 ID。</summary>
        public IReadOnlyCollection<int> UpdatedStockIds { get; } = UpdatedStockIds ?? [];
    }
}
