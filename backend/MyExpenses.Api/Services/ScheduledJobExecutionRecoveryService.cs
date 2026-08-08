namespace MyExpenses.Api.Services;

/// <summary>在 hosted services 開始前復原前次程序遺留的排程 execution。</summary>
public sealed class ScheduledJobExecutionRecoveryService
{
    private readonly ScheduledJobExecutionRepository _repository;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化使用共用 repository 與時間來源的啟動復原服務。</summary>
    public ScheduledJobExecutionRecoveryService(
        ScheduledJobExecutionRepository repository,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>原子標記遺留 Running execution 為 Interrupted。</summary>
    public Task<int> RecoverAsync(CancellationToken cancellationToken = default)
        => _repository.MarkRunningAsInterruptedAsync(
            DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc),
            cancellationToken);
}
