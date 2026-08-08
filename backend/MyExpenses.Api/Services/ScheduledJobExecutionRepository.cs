using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>表示排程 execution 時槽是否由目前呼叫成功保留。</summary>
public sealed record ScheduledJobExecutionReservation(
    ScheduledJobExecution Execution,
    bool Created);

/// <summary>提供排程 execution 的短交易持久化與安全查詢操作。</summary>
public sealed class ScheduledJobExecutionRepository
{
    private readonly AppDbContext _db;

    /// <summary>初始化使用 scoped database context 的 execution repository。</summary>
    public ScheduledJobExecutionRepository(AppDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>建立或取得同一排程時槽的 Running execution。</summary>
    public async Task<ScheduledJobExecution> CreateRunningAsync(
        ScheduledJobKey jobKey,
        DateTime scheduledForUtc,
        string scheduleTimeZoneId,
        DateOnly scheduledLocalDate,
        DateTime startedAtUtc,
        string? resultCode = null,
        string? safeMessage = null,
        CancellationToken cancellationToken = default)
        => (await CreateOrGetRunningAsync(
            jobKey,
            scheduledForUtc,
            scheduleTimeZoneId,
            scheduledLocalDate,
            startedAtUtc,
            resultCode,
            safeMessage,
            cancellationToken)).Execution;

    /// <summary>建立或取得 execution 並回報目前呼叫是否取得執行權。</summary>
    public async Task<ScheduledJobExecutionReservation> CreateOrGetRunningAsync(
        ScheduledJobKey jobKey,
        DateTime scheduledForUtc,
        string scheduleTimeZoneId,
        DateOnly scheduledLocalDate,
        DateTime startedAtUtc,
        string? resultCode = null,
        string? safeMessage = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedScheduledForUtc = NormalizeUtc(scheduledForUtc);
        var existing = await _db.ScheduledJobExecutions
            .SingleOrDefaultAsync(
                item => item.JobKey == jobKey && item.ScheduledForUtc == normalizedScheduledForUtc,
                cancellationToken);
        if (existing is not null)
            return new ScheduledJobExecutionReservation(existing, false);

        var execution = new ScheduledJobExecution
        {
            JobKey = jobKey,
            ScheduledForUtc = normalizedScheduledForUtc,
            ScheduleTimeZoneId = scheduleTimeZoneId.Trim(),
            ScheduledLocalDate = scheduledLocalDate,
            Status = ScheduledJobExecutionStatus.Running,
            StartedAtUtc = NormalizeUtc(startedAtUtc),
            AttemptCount = 0,
            ResultCode = ScheduledJobExecutionSafety.SanitizeResultCode(resultCode),
            SafeMessage = ScheduledJobExecutionSafety.SanitizeSafeMessage(safeMessage),
        };
        _db.ScheduledJobExecutions.Add(execution);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new ScheduledJobExecutionReservation(execution, true);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrent = await _db.ScheduledJobExecutions
                .SingleOrDefaultAsync(
                    item => item.JobKey == jobKey && item.ScheduledForUtc == normalizedScheduledForUtc,
                    cancellationToken);
            if (concurrent is not null)
                return new ScheduledJobExecutionReservation(concurrent, false);

            throw;
        }
    }

    /// <summary>依 ID 取得 execution，供狀態保存失敗時保留 Running 摘要。</summary>
    public Task<ScheduledJobExecution?> GetByIdAsync(
        long executionId,
        CancellationToken cancellationToken = default)
        => _db.ScheduledJobExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken);

    /// <summary>清除 workflow 留下的追蹤實體，隔離 execution metadata 保存。</summary>
    public void ClearTrackedState()
        => _db.ChangeTracker.Clear();

    /// <summary>依工作、狀態與 UTC 開始區間查詢 execution 並穩定降冪排序。</summary>
    public async Task<IReadOnlyList<ScheduledJobExecution>> QueryAsync(
        ScheduledJobKey? jobKey = null,
        ScheduledJobExecutionStatus? status = null,
        DateTime? startedAtUtcInclusive = null,
        DateTime? startedAtUtcExclusive = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(jobKey, status, startedAtUtcInclusive, startedAtUtcExclusive);
        query = query
            .OrderByDescending(item => item.StartedAtUtc)
            .ThenByDescending(item => item.Id);
        if (skip is > 0)
            query = query.Skip(skip.Value);
        if (take is > 0)
            query = query.Take(take.Value);

        return await query.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <summary>計算符合條件的 execution 數量，供分頁查詢使用。</summary>
    public Task<int> CountAsync(
        ScheduledJobKey? jobKey = null,
        ScheduledJobExecutionStatus? status = null,
        DateTime? startedAtUtcInclusive = null,
        DateTime? startedAtUtcExclusive = null,
        CancellationToken cancellationToken = default)
        => BuildQuery(jobKey, status, startedAtUtcInclusive, startedAtUtcExclusive)
            .CountAsync(cancellationToken);

    /// <summary>取得指定工作最近一次 execution 的唯讀摘要。</summary>
    public Task<ScheduledJobExecution?> GetLatestAsync(
        ScheduledJobKey jobKey,
        CancellationToken cancellationToken = default)
        => _db.ScheduledJobExecutions
            .AsNoTracking()
            .Where(item => item.JobKey == jobKey)
            .OrderByDescending(item => item.StartedAtUtc)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>在 workflow attempt 開始前遞增並持久化 attempt 次數。</summary>
    public async Task<ScheduledJobExecution?> IncrementAttemptAsync(
        long executionId,
        CancellationToken cancellationToken = default)
    {
        var execution = await _db.ScheduledJobExecutions
            .SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken);
        if (execution is null)
            return null;

        execution.AttemptCount++;
        await _db.SaveChangesAsync(cancellationToken);
        return execution;
    }

    /// <summary>以安全結果與 aggregate 數量完成 execution。</summary>
    public async Task<ScheduledJobExecution?> CompleteAsync(
        long executionId,
        ScheduledJobExecutionStatus status,
        DateTime completedAtUtc,
        int? targetCount,
        int succeededCount,
        int failedCount,
        int affectedCount,
        string? resultCode,
        string? safeMessage,
        CancellationToken cancellationToken = default)
    {
        _db.ChangeTracker.Clear();
        var execution = await _db.ScheduledJobExecutions
            .SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken);
        if (execution is null)
            return null;

        execution.Status = status;
        execution.CompletedAtUtc = NormalizeUtc(completedAtUtc);
        execution.TargetCount = targetCount;
        execution.SucceededCount = Math.Max(0, succeededCount);
        execution.FailedCount = Math.Max(0, failedCount);
        execution.AffectedCount = Math.Max(0, affectedCount);
        execution.ResultCode = ScheduledJobExecutionSafety.SanitizeResultCode(resultCode);
        execution.SafeMessage = ScheduledJobExecutionSafety.SanitizeSafeMessage(safeMessage);
        await _db.SaveChangesAsync(cancellationToken);
        return execution;
    }

    /// <summary>原子把所有遺留 Running execution 標記為程序重啟中斷。</summary>
    public async Task<int> MarkRunningAsInterruptedAsync(
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var completed = NormalizeUtc(completedAtUtc);
        _db.ChangeTracker.Clear();
        return await _db.ScheduledJobExecutions
            .Where(item => item.Status == ScheduledJobExecutionStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, ScheduledJobExecutionStatus.Interrupted)
                .SetProperty(item => item.CompletedAtUtc, completed)
                .SetProperty(item => item.ResultCode, "InterruptedByRestart")
                .SetProperty(item => item.SafeMessage, "服務重新啟動時中斷執行"), cancellationToken);
    }

    /// <summary>以 bounded batch 刪除完成時間嚴格早於 cutoff 的終止 execution。</summary>
    public async Task<int> CleanupCompletedAsync(
        DateTime cutoffUtc,
        int batchSize = 200,
        CancellationToken cancellationToken = default)
    {
        var ids = await _db.ScheduledJobExecutions
            .Where(item => item.CompletedAtUtc.HasValue
                && item.CompletedAtUtc.Value < NormalizeUtc(cutoffUtc)
                && item.Status != ScheduledJobExecutionStatus.Running)
            .OrderBy(item => item.CompletedAtUtc)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(Math.Clamp(batchSize, 1, 200))
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
            return 0;

        return await _db.ScheduledJobExecutions
            .Where(item => ids.Contains(item.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>建立帶有可重用篩選條件的 execution query。</summary>
    private IQueryable<ScheduledJobExecution> BuildQuery(
        ScheduledJobKey? jobKey,
        ScheduledJobExecutionStatus? status,
        DateTime? startedAtUtcInclusive,
        DateTime? startedAtUtcExclusive)
    {
        var query = _db.ScheduledJobExecutions.AsQueryable();
        if (jobKey.HasValue)
            query = query.Where(item => item.JobKey == jobKey.Value);
        if (status.HasValue)
            query = query.Where(item => item.Status == status.Value);
        if (startedAtUtcInclusive.HasValue)
        {
            var start = NormalizeUtc(startedAtUtcInclusive.Value);
            query = query.Where(item => item.StartedAtUtc >= start);
        }
        if (startedAtUtcExclusive.HasValue)
        {
            var end = NormalizeUtc(startedAtUtcExclusive.Value);
            query = query.Where(item => item.StartedAtUtc < end);
        }

        return query;
    }

    /// <summary>將日期時間標準化為明確的 UTC kind。</summary>
    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Local)
            value = value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

}
