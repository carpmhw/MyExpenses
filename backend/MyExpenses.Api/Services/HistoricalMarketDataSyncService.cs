using System.Runtime.ExceptionServices;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>在台灣市場平日 23:30 執行歷史行情同步的背景服務。</summary>
public sealed class HistoricalMarketDataSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HistoricalMarketDataSyncService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化固定台灣時區的歷史行情背景服務。</summary>
    public HistoricalMarketDataSyncService(
        IServiceScopeFactory scopeFactory,
        ILogger<HistoricalMarketDataSyncService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>背景服務迴圈，依下次平日夜間時間執行同步 execution。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Historical market data sync service started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = UtcNow();
                var scheduledForUtc = CalculateNextUpdateUtc(nowUtc);
                await Task.Delay(scheduledForUtc - nowUtc, _timeProvider, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;

                await RunScheduledExecutionAsync(scheduledForUtc, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Historical market data sync schedule loop failed");
                await Task.Delay(TimeSpan.FromMinutes(1), _timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>計算距離台灣時間下次 23:30 同步的延遲。</summary>
    public static TimeSpan CalculateDelayToNextUpdate(DateTime utcNow)
    {
        var normalized = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return CalculateNextUpdateUtc(normalized) - normalized;
    }

    /// <summary>計算下次平日台灣時間 23:30 的 UTC 時間。</summary>
    public static DateTime CalculateNextUpdateUtc(DateTime utcNow)
        => BusinessScheduleCalculator.CalculateHistoricalSyncNextRunUtc(utcNow);

    /// <summary>建立指定 slot 的 scope 並委派 typed historical batch 給 runner。</summary>
    private async Task RunScheduledExecutionAsync(
        DateTime scheduledForUtc,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduledJobRunner>();
        var synchronizer = scope.ServiceProvider.GetRequiredService<HistoricalMarketDataSynchronizer>();
        var localDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(scheduledForUtc, DateTimeKind.Utc),
                BusinessScheduleCalculator.TaiwanTimeZone));
        await runner.RunAsync(
            ScheduledJobKey.HistoricalMarketDataSync,
            scheduledForUtc,
            BusinessScheduleCalculator.TaiwanTimeZone.Id,
            localDate,
            (context, token) => RunWorkflowAsync(synchronizer, context, localDate, token),
            cancellationToken);
    }

    /// <summary>執行單次歷史同步，並在 persistence 中止時先保留 partial aggregate 再重拋原始例外。</summary>
    private static async Task<ScheduledJobWorkflowResult> RunWorkflowAsync(
        HistoricalMarketDataSynchronizer synchronizer,
        ScheduledJobWorkflowContext context,
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await synchronizer.SyncAsync(localDate, cancellationToken, context.FrozenTargetKeys);
            return MapResult(result);
        }
        catch (HistoricalMarketDataPartialFailureException exception)
        {
            context.Aggregation.Apply(MapResult(exception.PartialResult));
            ExceptionDispatchInfo.Capture(exception.InnerException ?? exception).Throw();
            throw;
        }
    }

    /// <summary>將歷史同步 typed batch result 映射為共用 runner envelope。</summary>
    private static ScheduledJobWorkflowResult MapResult(HistoricalMarketDataSyncResult result)
    {
        var targetCount = result.TargetCount ?? result.ProcessedInstrumentCount;
        var succeeded = result.SuccessfulInstrumentCount;
        var failed = result.FailedInstrumentCount;
        var outcome = targetCount == 0
            ? ScheduledJobWorkflowOutcome.NoWork
            : succeeded == targetCount
                ? ScheduledJobWorkflowOutcome.Succeeded
                : succeeded > 0
                    ? ScheduledJobWorkflowOutcome.PartiallySucceeded
                    : ScheduledJobWorkflowOutcome.Failed;
        return new ScheduledJobWorkflowResult
        {
            Outcome = outcome,
            Retryability = result.RetryableFailure
                ? ScheduledJobRetryClassification.Retryable
                : outcome == ScheduledJobWorkflowOutcome.Succeeded || outcome == ScheduledJobWorkflowOutcome.NoWork
                    ? ScheduledJobRetryClassification.None
                    : ScheduledJobRetryClassification.Permanent,
            TargetsEnumerated = result.TargetCount.HasValue || result.ProcessedInstrumentCount > 0,
            TargetCount = result.TargetCount,
            SucceededCount = succeeded,
            FailedCount = failed,
            AffectedCount = result.AffectedCount,
            TargetKeys = result.TargetKeys ?? [],
            SucceededTargetKeys = result.SuccessfulTargetKeys ?? [],
            FailedTargetCodes = result.FailedTargetCodes ?? new Dictionary<string, string>(StringComparer.Ordinal),
            AffectedRowKeys = result.AffectedRowKeys ?? [],
            ResultCode = result.ResultCode,
            SafeMessage = "歷史行情批次已完成 aggregate 處理",
        };
    }

    /// <summary>取得明確 UTC 現在時間。</summary>
    private DateTime UtcNow()
        => DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
}
