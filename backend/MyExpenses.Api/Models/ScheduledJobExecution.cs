namespace MyExpenses.Api.Models;

/// <summary>識別目前支援的業務排程工作。</summary>
public enum ScheduledJobKey
{
    AutomaticSnapshot,
    StockPriceUpdate,
    HistoricalMarketDataSync,
}

/// <summary>描述業務排程 execution 的終態與進行中狀態。</summary>
public enum ScheduledJobExecutionStatus
{
    Running,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Canceled,
    Interrupted,
}

/// <summary>保存單一排程時槽的安全執行摘要。</summary>
public sealed class ScheduledJobExecution
{
    public long Id { get; set; }
    public ScheduledJobKey JobKey { get; set; }
    public DateTime ScheduledForUtc { get; set; }
    public string ScheduleTimeZoneId { get; set; } = string.Empty;
    public DateOnly ScheduledLocalDate { get; set; }
    public ScheduledJobExecutionStatus Status { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public int? TargetCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int AffectedCount { get; set; }
    public string? ResultCode { get; set; }
    public string? SafeMessage { get; set; }
}
