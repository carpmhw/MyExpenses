using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Extensions.Logging;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>描述 workflow 結果是否允許在同一 execution 內重新嘗試。</summary>
public enum ScheduledJobRetryClassification
{
    None,
    Retryable,
    Permanent,
}

/// <summary>描述 workflow 在單次 attempt 觀察到的工作結果。</summary>
public enum ScheduledJobWorkflowOutcome
{
    NoWork,
    Succeeded,
    PartiallySucceeded,
    Failed,
}

/// <summary>封裝 workflow 的安全批次結果與 execution-local 目標身分。</summary>
public sealed record ScheduledJobWorkflowResult
{
    public ScheduledJobWorkflowOutcome Outcome { get; init; }
    public ScheduledJobRetryClassification Retryability { get; init; }
    public bool TargetsEnumerated { get; init; }
    public int? TargetCount { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public int AffectedCount { get; init; }
    /// <summary>供應商回傳的已正規化資料列數量。</summary>
    public int ProviderRecordCount { get; init; }
    /// <summary>已成功寫入業務資料的目標數量。</summary>
    public int UpdatedCount { get; init; }
    /// <summary>供應商資料中沒有對應目前目標的資料列數量。</summary>
    public int UnmatchedCount { get; init; }
    /// <summary>供應商資料中價格無效的資料列數量。</summary>
    public int InvalidCount { get; init; }
    public IReadOnlyCollection<string> TargetKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> SucceededTargetKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> FailedTargetCodes { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
    public IReadOnlyCollection<string> AffectedRowKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> FailureCodes { get; init; } = Array.Empty<string>();
    public string? ResultCode { get; init; }
    public string? SafeMessage { get; init; }

    /// <summary>建立已列舉且沒有符合條件工作的成功結果。</summary>
    public static ScheduledJobWorkflowResult NoWork(string resultCode = "NoEligibleTargets")
        => new()
        {
            Outcome = ScheduledJobWorkflowOutcome.NoWork,
            Retryability = ScheduledJobRetryClassification.None,
            TargetsEnumerated = true,
            TargetCount = 0,
            ResultCode = resultCode,
        };
}

/// <summary>提供 workflow 使用的 execution 身分與跨 attempt 聚合器。</summary>
public sealed record ScheduledJobWorkflowContext(
    long ExecutionId,
    ScheduledJobKey JobKey,
    DateTime ScheduledForUtc,
    int Attempt,
    ScheduledJobExecutionAccumulator Aggregation)
{
    /// <summary>取得第一次成功列舉後凍結的目標集合，尚未凍結時回傳 null。</summary>
    public IReadOnlyCollection<string>? FrozenTargetKeys
        => Aggregation.TargetsEnumerated ? Aggregation.FrozenTargetKeys : null;
}

/// <summary>保存單一 execution 內不應重複計數的目標與業務 row disposition。</summary>
public sealed class ScheduledJobExecutionAccumulator
{
    private readonly List<string> _targetKeys = [];
    private readonly HashSet<string> _targetKeyMembership = new(StringComparer.Ordinal);
    private readonly HashSet<string> _succeededTargetKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failedTargetCodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _affectedRowKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failureCodes = new(StringComparer.Ordinal);
    private readonly List<string> _preEnumerationFailureCodes = [];
    private int? _fallbackTargetCount;
    private int _fallbackSucceededCount;
    private int _fallbackFailedCount;
    private int _fallbackAffectedCount;

    /// <summary>表示至少一次 attempt 已成功列舉並凍結目標集合。</summary>
    public bool TargetsEnumerated { get; private set; }

    /// <summary>取得 execution-local 凍結目標集合的唯讀複本。</summary>
    public IReadOnlyCollection<string> FrozenTargetKeys
        => _targetKeys.ToArray();

    /// <summary>套用單次 workflow 結果並保留跨 attempt 的唯一 disposition。</summary>
    public void Apply(ScheduledJobWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.TargetsEnumerated)
        {
            AddFailureCode(_preEnumerationFailureCodes, result.ResultCode);
            foreach (var code in result.FailureCodes)
                AddFailureCode(_preEnumerationFailureCodes, code);
        }
        else
        {
            FreezeTargets(result);
            ApplySucceededTargets(result);
            ApplyFailedTargets(result);
            _fallbackSucceededCount = Math.Max(_fallbackSucceededCount, result.SucceededCount);
            _fallbackFailedCount = Math.Max(_fallbackFailedCount, result.FailedCount);
            _fallbackAffectedCount = Math.Max(_fallbackAffectedCount, result.AffectedCount);
            foreach (var code in result.FailureCodes)
                AddFailureCode(_failureCodes, code);
            if (result.FailedCount > 0)
                AddFailureCode(_failureCodes, result.ResultCode);
        }

        foreach (var key in result.AffectedRowKeys)
        {
            var normalized = NormalizeKey(key);
            if (normalized is not null)
                _affectedRowKeys.Add(normalized);
        }
    }

    /// <summary>建立適合寫入 execution 的唯一目標 aggregate。</summary>
    public ScheduledJobExecutionAggregate BuildAggregate(ScheduledJobWorkflowResult? lastResult)
    {
        if (!TargetsEnumerated)
        {
            var preEnumerationCodes = _preEnumerationFailureCodes
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var preEnumerationResultCode = preEnumerationCodes.Length switch
            {
                1 => preEnumerationCodes[0],
                > 1 => "MultipleFailures",
                _ => "TargetEnumerationFailed",
            };
            return new ScheduledJobExecutionAggregate(
                null,
                0,
                0,
                _affectedRowKeys.Count > 0 ? _affectedRowKeys.Count : _fallbackAffectedCount,
                preEnumerationResultCode,
                BuildSafeMessage(null, 0, 0, _affectedRowKeys.Count));
        }

        var targetCount = _targetKeys.Count > 0
            ? _targetKeys.Count
            : _fallbackTargetCount ?? lastResult?.TargetCount ?? 0;
        var succeededCount = _targetKeys.Count > 0
            ? _succeededTargetKeys.Count
            : Math.Min(targetCount, _fallbackSucceededCount);
        var failedCount = _targetKeys.Count > 0
            ? _targetKeys.Count - succeededCount
            : Math.Max(_fallbackFailedCount, targetCount - succeededCount);
        var affectedCount = _affectedRowKeys.Count > 0
            ? _affectedRowKeys.Count
            : _fallbackAffectedCount;

        var terminalResultCode = ResolveTerminalResultCode(targetCount, succeededCount, failedCount, lastResult);
        return new ScheduledJobExecutionAggregate(
            targetCount,
            succeededCount,
            failedCount,
            affectedCount,
            terminalResultCode,
            BuildSafeMessage(targetCount, succeededCount, failedCount, affectedCount));
    }

    /// <summary>凍結第一次成功列舉的唯一目標集合，忽略後續 attempt 新增目標。</summary>
    private void FreezeTargets(ScheduledJobWorkflowResult result)
    {
        if (TargetsEnumerated)
            return;

        TargetsEnumerated = true;
        var normalizedKeys = result.TargetKeys
            .Select(NormalizeKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var key in normalizedKeys)
        {
            if (_targetKeyMembership.Add(key))
                _targetKeys.Add(key);
        }
        _fallbackTargetCount = normalizedKeys.Count > 0
            ? normalizedKeys.Count
            : result.TargetCount.GetValueOrDefault();
    }

    /// <summary>套用已確認成功目標且避免把成功降回失敗。</summary>
    private void ApplySucceededTargets(ScheduledJobWorkflowResult result)
    {
        if (_targetKeys.Count == 0)
            return;

        foreach (var key in result.SucceededTargetKeys.Select(NormalizeKey).Where(key => key is not null))
        {
            var normalized = key!;
            if (!_targetKeyMembership.Contains(normalized))
                continue;
            _succeededTargetKeys.Add(normalized);
            _failedTargetCodes.Remove(normalized);
        }
    }

    /// <summary>套用尚未成功目標的安全 failure code。</summary>
    private void ApplyFailedTargets(ScheduledJobWorkflowResult result)
    {
        if (_targetKeys.Count == 0)
            return;

        foreach (var pair in result.FailedTargetCodes)
        {
            var key = NormalizeKey(pair.Key);
            var code = NormalizeResultCode(pair.Value);
            if (key is null || code is null || !_targetKeyMembership.Contains(key) || _succeededTargetKeys.Contains(key))
                continue;
            _failedTargetCodes[key] = code;
            AddFailureCode(_failureCodes, code);
        }
    }

    /// <summary>依唯一目標 disposition 推導終態 result code。</summary>
    private string ResolveTerminalResultCode(
        int targetCount,
        int succeededCount,
        int failedCount,
        ScheduledJobWorkflowResult? lastResult)
    {
        if (targetCount == 0)
            return lastResult?.Outcome == ScheduledJobWorkflowOutcome.NoWork
                && !string.IsNullOrWhiteSpace(lastResult.ResultCode)
                ? NormalizeResultCode(lastResult.ResultCode) ?? "NoEligibleTargets"
                : "NoEligibleTargets";
        if (succeededCount == targetCount)
            return "Completed";
        if (succeededCount > 0 && failedCount > 0)
            return "IncompleteTargets";

        var failureCodeSource = _targetKeys.Count > 0
            ? _targetKeys
                .Where(key => !_succeededTargetKeys.Contains(key))
                .Select(key => _failedTargetCodes.TryGetValue(key, out var code)
                    ? code
                    : NormalizeResultCode(lastResult?.ResultCode) ?? "Failed")
            : _failureCodes;
        var codes = failureCodeSource
            .Select(NormalizeResultCode)
            .Where(code => code is not null)
            .Select(code => code!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (codes.Length == 1)
            return codes[0];
        if (codes.Length > 1)
            return "MultipleFailures";
        return NormalizeResultCode(lastResult?.ResultCode) ?? "Failed";
    }

    /// <summary>建立只包含 aggregate 數量的安全 execution 訊息。</summary>
    private static string BuildSafeMessage(int? targetCount, int succeededCount, int failedCount, int affectedCount)
        => targetCount.HasValue
            ? $"排程結果：目標 {targetCount.Value}，成功 {succeededCount}，失敗 {failedCount}，受影響 {affectedCount}。"
            : "排程目標列舉失敗，未建立可用目標摘要。";

    /// <summary>加入非空且 bounded 的 failure code。</summary>
    private static void AddFailureCode(ICollection<string> destination, string? value)
    {
        var normalized = NormalizeResultCode(value);
        if (normalized is not null)
            destination.Add(normalized);
    }

    /// <summary>正規化 execution-local 目標身分，不將其保存至使用者摘要。</summary>
    private static string? NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

    /// <summary>只接受 bounded machine-readable result code。</summary>
    private static string? NormalizeResultCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length > 80)
            return null;
        return normalized.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            ? normalized
            : null;
    }
}

/// <summary>提供 runner 完成 execution 所需的 aggregate 欄位。</summary>
public sealed record ScheduledJobExecutionAggregate(
    int? TargetCount,
    int SucceededCount,
    int FailedCount,
    int AffectedCount,
    string ResultCode,
    string SafeMessage);

/// <summary>描述共用 runner 的 retry 與 retention 參數。</summary>
public sealed class ScheduledJobRunnerOptions
{
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    public int RetentionDays { get; set; } = 90;
    public int CleanupBatchSize { get; set; } = 200;
}

/// <summary>以一致生命週期執行業務 workflow 並保存安全 execution 摘要。</summary>
public sealed class ScheduledJobRunner
{
    private readonly ScheduledJobExecutionRepository _repository;
    private readonly ILogger<ScheduledJobRunner> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ScheduledJobRunnerOptions _options;

    /// <summary>初始化注入 repository、structured logger、時間來源與 runner 參數。</summary>
    public ScheduledJobRunner(
        ScheduledJobExecutionRepository repository,
        ILogger<ScheduledJobRunner> logger,
        TimeProvider? timeProvider = null,
        ScheduledJobRunnerOptions? options = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new ScheduledJobRunnerOptions();
    }

    /// <summary>建立單一排程時槽 execution 並執行最多三次 workflow attempt。</summary>
    public async Task<ScheduledJobExecution> RunAsync(
        ScheduledJobKey jobKey,
        DateTime scheduledForUtc,
        string scheduleTimeZoneId,
        DateOnly scheduledLocalDate,
        Func<ScheduledJobWorkflowContext, CancellationToken, Task<ScheduledJobWorkflowResult>> workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var normalizedScheduledForUtc = NormalizeUtc(scheduledForUtc);
        var reservation = await _repository.CreateOrGetRunningAsync(
            jobKey,
            normalizedScheduledForUtc,
            scheduleTimeZoneId,
            scheduledLocalDate,
            UtcNow(),
            cancellationToken: cancellationToken);
        if (!reservation.Created)
            return reservation.Execution;

        var aggregation = new ScheduledJobExecutionAccumulator();
        ScheduledJobWorkflowResult? lastResult = null;
        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 3);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _repository.ClearTrackedState();
                cancellationToken.ThrowIfCancellationRequested();
                await _repository.IncrementAttemptAsync(reservation.Execution.Id, CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var canceledAggregate = aggregation.BuildAggregate(lastResult);
                return await CompleteCanceledAsync(
                    jobKey,
                    normalizedScheduledForUtc,
                    reservation.Execution.Id,
                    canceledAggregate);
            }
            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["JobKey"] = jobKey.ToString(),
                ["ExecutionId"] = reservation.Execution.Id,
                ["ScheduledForUtc"] = normalizedScheduledForUtc,
                ["Attempt"] = attempt,
            });
            _logger.LogInformation("Scheduled job attempt started");

            try
            {
                lastResult = await workflow(
                    new ScheduledJobWorkflowContext(
                        reservation.Execution.Id,
                        jobKey,
                        normalizedScheduledForUtc,
                        attempt,
                        aggregation),
                    cancellationToken);
                aggregation.Apply(lastResult);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var canceledAggregate = aggregation.BuildAggregate(lastResult);
                return await CompleteCanceledAsync(
                    jobKey,
                    normalizedScheduledForUtc,
                    reservation.Execution.Id,
                    canceledAggregate);
            }
            catch (Exception exception)
            {
                lastResult = CreateUnexpectedFailure(exception);
                aggregation.Apply(lastResult);
                _logger.LogError(
                    "Scheduled job attempt raised bounded failure with result code {ResultCode}",
                    lastResult.ResultCode);
            }

            if (lastResult.Retryability == ScheduledJobRetryClassification.Retryable
                && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "Scheduled job attempt failed and will retry with result code {ResultCode}",
                    NormalizeLogCode(lastResult.ResultCode));
                try
                {
                    await Task.Delay(NormalizeRetryDelay(), _timeProvider, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    var canceledAggregate = aggregation.BuildAggregate(lastResult);
                    return await CompleteCanceledAsync(
                        jobKey,
                        normalizedScheduledForUtc,
                        reservation.Execution.Id,
                        canceledAggregate);
                }
                catch (OperationCanceledException exception)
                {
                    lastResult = CreateUnexpectedFailure(exception);
                    aggregation.Apply(lastResult);
                    _logger.LogError(
                        "Scheduled job retry delay raised bounded failure with result code {ResultCode}",
                        lastResult.ResultCode);
                }
                continue;
            }

            break;
        }

        var aggregate = aggregation.BuildAggregate(lastResult);
        var status = DetermineStatus(aggregate);
        var completed = await CompleteExecutionAsync(
            reservation.Execution.Id,
            status,
            aggregate.TargetCount,
            aggregate.SucceededCount,
            aggregate.FailedCount,
            aggregate.AffectedCount,
            aggregate.ResultCode,
            aggregate.SafeMessage);
        await CleanupAsync();
        LogExecutionResult(jobKey, normalizedScheduledForUtc, completed);
        return completed;
    }

    /// <summary>保存取消終態、執行保留清理並寫入帶有 execution 關聯的完成 log。</summary>
    private async Task<ScheduledJobExecution> CompleteCanceledAsync(
        ScheduledJobKey jobKey,
        DateTime scheduledForUtc,
        long executionId,
        ScheduledJobExecutionAggregate aggregate)
    {
        var completed = await CompleteExecutionAsync(
            executionId,
            ScheduledJobExecutionStatus.Canceled,
            aggregate.TargetCount,
            aggregate.SucceededCount,
            aggregate.FailedCount,
            aggregate.AffectedCount,
            "Canceled",
            "排程執行已取消");
        await CleanupAsync();
        LogExecutionResult(jobKey, scheduledForUtc, completed);
        return completed;
    }

    /// <summary>以 execution scope 寫入終態摘要或保存失敗的高嚴重度 log。</summary>
    private void LogExecutionResult(
        ScheduledJobKey jobKey,
        DateTime scheduledForUtc,
        ScheduledJobExecution execution)
    {
        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["JobKey"] = jobKey.ToString(),
            ["ExecutionId"] = execution.Id,
            ["ScheduledForUtc"] = scheduledForUtc,
            ["Attempt"] = execution.AttemptCount,
        });
        if (execution.Status == ScheduledJobExecutionStatus.Running)
        {
            _logger.LogCritical(
                "Scheduled job execution remains Running because terminal status persistence failed; "
                + "execution id {ExecutionId}",
                execution.Id);
            return;
        }

        if (execution.Status == ScheduledJobExecutionStatus.Canceled)
        {
            _logger.LogInformation(
                "Scheduled job execution canceled with result code {ResultCode}; "
                + "target count {TargetCount}, succeeded count {SucceededCount}, failed count {FailedCount}, "
                + "affected count {AffectedCount}",
                execution.ResultCode,
                execution.TargetCount,
                execution.SucceededCount,
                execution.FailedCount,
                execution.AffectedCount);
            return;
        }

        _logger.LogInformation(
            "Scheduled job execution completed with status {Status} and result code {ResultCode}; "
            + "target count {TargetCount}, succeeded count {SucceededCount}, failed count {FailedCount}, "
            + "affected count {AffectedCount}",
            execution.Status,
            execution.ResultCode,
            execution.TargetCount,
            execution.SucceededCount,
            execution.FailedCount,
            execution.AffectedCount);
    }

    /// <summary>將 execution 更新為終態；保存失敗時保留 Running 並記錄高嚴重度 log。</summary>
    private async Task<ScheduledJobExecution> CompleteExecutionAsync(
        long executionId,
        ScheduledJobExecutionStatus status,
        int? targetCount,
        int succeededCount,
        int failedCount,
        int affectedCount,
        string resultCode,
        string safeMessage)
    {
        try
        {
            _repository.ClearTrackedState();
            var execution = await _repository.CompleteAsync(
                executionId,
                status,
                UtcNow(),
                targetCount,
                succeededCount,
                failedCount,
                affectedCount,
                resultCode,
                safeMessage,
                CancellationToken.None);
            if (execution is not null)
                return execution;
        }
        catch (Exception)
        {
            _logger.LogCritical(
                "Scheduled job execution status persistence failed with safe code {ResultCode}",
                "ExecutionPersistenceFailed");
        }

        try
        {
            var fallback = await _repository.GetByIdAsync(executionId, CancellationToken.None);
            return fallback ?? CreatePersistenceFailureFallback(executionId);
        }
        catch (Exception)
        {
            _logger.LogCritical(
                "Scheduled job execution fallback query failed with safe code {ResultCode}",
                "ExecutionPersistenceFailed");
            return CreatePersistenceFailureFallback(executionId);
        }
    }

    /// <summary>建立保守的 detached Running 摘要，避免狀態保存故障逸出 workflow。</summary>
    private static ScheduledJobExecution CreatePersistenceFailureFallback(long executionId)
        => new()
        {
            Id = executionId,
            Status = ScheduledJobExecutionStatus.Running,
            ResultCode = "ExecutionPersistenceFailed",
            SafeMessage = "排程執行狀態保存失敗，保留執行中狀態。",
        };

    /// <summary>以 best-effort bounded cleanup 清理九十天前的終止 execution。</summary>
    private async Task CleanupAsync()
    {
        try
        {
            var retentionDays = Math.Max(1, _options.RetentionDays);
            await _repository.CleanupCompletedAsync(
                UtcNow().AddDays(-retentionDays),
                _options.CleanupBatchSize,
                CancellationToken.None);
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Scheduled job execution retention cleanup failed with safe code {ResultCode}",
                "RetentionCleanupFailed");
        }
    }

    /// <summary>將 retry delay 限制為非負時間。</summary>
    private TimeSpan NormalizeRetryDelay()
        => _options.RetryDelay < TimeSpan.Zero ? TimeSpan.Zero : _options.RetryDelay;

    /// <summary>依 execution-level aggregate 數量推導終態。</summary>
    private static ScheduledJobExecutionStatus DetermineStatus(ScheduledJobExecutionAggregate aggregate)
    {
        if (!aggregate.TargetCount.HasValue)
            return ScheduledJobExecutionStatus.Failed;
        if (aggregate.TargetCount.Value == 0 || aggregate.FailedCount == 0)
            return ScheduledJobExecutionStatus.Succeeded;
        return aggregate.SucceededCount > 0
            ? ScheduledJobExecutionStatus.PartiallySucceeded
            : ScheduledJobExecutionStatus.Failed;
    }

    /// <summary>把未預期例外轉成不含原始訊息的 bounded workflow failure。</summary>
    private static ScheduledJobWorkflowResult CreateUnexpectedFailure(Exception exception)
    {
        var retryable = exception is OperationCanceledException
            || RetryClassification.IsRetryable(exception);
        return new()
        {
            Outcome = ScheduledJobWorkflowOutcome.Failed,
            Retryability = retryable
                ? ScheduledJobRetryClassification.Retryable
                : ScheduledJobRetryClassification.Permanent,
            TargetsEnumerated = false,
            ResultCode = retryable
                ? "TransientFailure"
                : "UnexpectedFailure",
            SafeMessage = "排程執行失敗",
        };
    }

    /// <summary>取得明確 UTC 現在時間。</summary>
    private DateTime UtcNow()
        => DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);

    /// <summary>將日期時間標準化為 UTC。</summary>
    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Local)
            value = value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    /// <summary>限制程序 log 中的 result code 為安全短字串。</summary>
    private static string NormalizeLogCode(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > 80
            ? "UnknownFailure"
            : value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
                ? value
                : "UnknownFailure";
}

/// <summary>集中判斷常見 transient provider、網路與 SQLite 鎖定錯誤。</summary>
public static class RetryClassification
{
    /// <summary>判斷例外是否適合在同一 execution 內重試。</summary>
    public static bool IsRetryable(Exception exception)
    {
        if (exception is TimeoutException)
            return true;
        if (exception is HttpRequestException httpRequestException)
        {
            if (!httpRequestException.StatusCode.HasValue)
                return true;

            var statusCode = httpRequestException.StatusCode.Value;
            var numericStatusCode = (int)statusCode;
            return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || numericStatusCode is >= 500 and <= 599;
        }
        if (exception is Microsoft.Data.Sqlite.SqliteException sqlite)
            return sqlite.SqliteErrorCode is 5 or 6;
        if (exception.InnerException is not null)
            return IsRetryable(exception.InnerException);
        return false;
    }
}
