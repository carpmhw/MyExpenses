using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>依持股市場分流 provider 並以短 transaction 保存目前價格的 workflow。</summary>
public sealed class CurrentStockPriceWorkflow
{
    private readonly AppDbContext _db;
    private readonly ICurrentPriceProvider _twseProvider;
    private readonly ICurrentPriceProvider _tpexProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CurrentStockPriceWorkflow>? _logger;

    /// <summary>初始化目前價格 workflow 與兩個市場 provider。</summary>
    public CurrentStockPriceWorkflow(
        AppDbContext db,
        ICurrentPriceProvider twseProvider,
        ICurrentPriceProvider tpexProvider,
        TimeProvider? timeProvider = null,
        ILogger<CurrentStockPriceWorkflow>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _twseProvider = twseProvider ?? throw new ArgumentNullException(nameof(twseProvider));
        _tpexProvider = tpexProvider ?? throw new ArgumentNullException(nameof(tpexProvider));
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

        var targets = stocks
            .Select(stock => new StockPriceTarget(
                stock.Id.ToString(CultureInfo.InvariantCulture),
                stock.Id,
                NormalizeSymbol(stock.Symbol),
                stock.Market))
            .ToList();
        if (frozenTargetKeys is not null)
        {
            var frozen = frozenTargetKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .ToHashSet(StringComparer.Ordinal);
            targets = targets
                .Where(target => frozen.Contains(target.Key))
                .ToList();
        }
        if (targets.Count == 0)
            return ScheduledJobWorkflowResult.NoWork("NoEligibleTargets");

        var succeeded = new List<string>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        var affected = new List<string>();
        var retryableFailure = false;
        var providerRecordCount = 0;
        var unmatchedCount = 0;
        var invalidCount = 0;
        foreach (var target in targets.Where(target => target.Symbol.Length == 0))
            failed[target.Key] = "InvalidSymbol";

        foreach (var group in targets
                     .Where(target => target.Symbol.Length > 0 && target.Market is StockMarket.Twse or StockMarket.Tpex)
                     .GroupBy(target => target.Market))
        {
            var provider = group.Key == StockMarket.Twse ? _twseProvider : _tpexProvider;
            var providerResult = await provider.FetchAsync(cancellationToken);
            if (providerResult.Failure is not null)
            {
                var failure = providerResult.Failure;
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
                retryableFailure |= failure.Retryable;
                foreach (var target in group)
                    failed[target.Key] = failure.Code;
                continue;
            }

            providerRecordCount += providerResult.Records.Count;
            var targetSymbols = group
                .Select(target => target.Symbol)
                .ToHashSet(StringComparer.Ordinal);
            unmatchedCount += providerResult.Records.Count(record =>
                !targetSymbols.Contains(NormalizeSymbol(record.Symbol)));
            invalidCount += providerResult.Records.Count(record =>
                !record.Price.HasValue || record.Price.Value <= 0m);
            var records = providerResult.Records
                .GroupBy(record => NormalizeSymbol(record.Symbol))
                .ToDictionary(grouping => grouping.Key, grouping => grouping.Last(), StringComparer.Ordinal);
            foreach (var symbolGroup in group.GroupBy(target => target.Symbol))
            {
                if (!records.TryGetValue(symbolGroup.Key, out var record))
                {
                    foreach (var target in symbolGroup)
                        failed[target.Key] = "NoMatchingPrice";
                    continue;
                }

                if (!record.Price.HasValue || record.Price.Value <= 0m)
                {
                    foreach (var target in symbolGroup)
                        failed[target.Key] = "InvalidPrice";
                    continue;
                }

                var targetIds = symbolGroup.Select(target => target.Id).ToArray();
                var persistence = await PersistPriceAsync(
                    targetIds,
                    group.Key,
                    symbolGroup.Key,
                    record.Price.Value,
                    cancellationToken);
                if (!persistence.Succeeded)
                {
                    retryableFailure |= persistence.Retryable;
                    foreach (var target in symbolGroup)
                        failed[target.Key] = persistence.Code;
                    continue;
                }

                foreach (var target in symbolGroup)
                {
                    succeeded.Add(target.Key);
                    affected.Add(target.Key);
                }
            }
        }

        foreach (var target in targets.Where(target => target.Market == StockMarket.Unknown))
            failed[target.Key] = "UnknownMarket";

        var succeededKeys = succeeded.Distinct(StringComparer.Ordinal).ToArray();
        var failedKeys = failed
            .Where(pair => !succeededKeys.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var resultCode = ResolveResultCode(targets.Count, succeededKeys.Length, failedKeys.Values);
        return new ScheduledJobWorkflowResult
        {
            Outcome = succeededKeys.Length == targets.Count
                ? ScheduledJobWorkflowOutcome.Succeeded
                : succeededKeys.Length > 0
                    ? ScheduledJobWorkflowOutcome.PartiallySucceeded
                    : ScheduledJobWorkflowOutcome.Failed,
            Retryability = retryableFailure
                ? ScheduledJobRetryClassification.Retryable
                : ScheduledJobRetryClassification.Permanent,
            TargetsEnumerated = true,
            TargetCount = targets.Count,
            SucceededCount = succeededKeys.Length,
            FailedCount = failedKeys.Count,
            AffectedCount = affected.Distinct(StringComparer.Ordinal).Count(),
            ProviderRecordCount = providerRecordCount,
            UpdatedCount = succeededKeys.Length,
            UnmatchedCount = unmatchedCount,
            InvalidCount = invalidCount,
            TargetKeys = targets.Select(target => target.Key).ToArray(),
            SucceededTargetKeys = succeededKeys,
            FailedTargetCodes = failedKeys,
            AffectedRowKeys = affected.Distinct(StringComparer.Ordinal).ToArray(),
            ResultCode = resultCode,
            SafeMessage = "目前價格批次已完成 aggregate 處理",
        };
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
            if (stocks.Count != stockIds.Count
                || stocks.Any(stock => stock.Market != expectedMarket
                    || NormalizeSymbol(stock.Symbol) != expectedSymbol))
                return new PricePersistenceResult(false, false, "TargetChanged");

            var nowUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
            foreach (var stock in stocks)
            {
                stock.CurrentPrice = price;
                stock.LastPriceUpdate = nowUtc;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return new PricePersistenceResult(true, false, "Completed");
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

    /// <summary>保存 execution-local 目前價格目標欄位。</summary>
    private sealed record StockPriceTarget(string Key, int Id, string Symbol, StockMarket Market);

    /// <summary>保存單次持股價格 transaction 的安全結果。</summary>
    private sealed record PricePersistenceResult(bool Succeeded, bool Retryable, string Code);
}
