using MyExpenses.Api.Services;

namespace MyExpenses.Api.Services;

/// <summary>在台灣市場平日 23:30 執行歷史行情同步的背景服務。</summary>
public sealed class HistoricalMarketDataSyncService : BackgroundService
{
    private static readonly TimeZoneInfo TaiwanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HistoricalMarketDataSyncService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化固定台灣時區的歷史行情背景服務。</summary>
    public HistoricalMarketDataSyncService(
        IServiceScopeFactory scopeFactory,
        ILogger<HistoricalMarketDataSyncService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>背景服務迴圈，依下次平日夜間時間執行同步。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Historical market data sync service started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
                var delay = CalculateDelayToNextUpdate(nowUtc);
                await Task.Delay(delay, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;

                using var scope = _scopeFactory.CreateScope();
                var synchronizer = scope.ServiceProvider.GetRequiredService<HistoricalMarketDataSynchronizer>();
                var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc),
                    TaiwanTimeZone));
                var result = await synchronizer.SyncAsync(localDate, stoppingToken);
                _logger.LogInformation(
                    "Historical market data sync completed: {Processed} processed, {Succeeded} succeeded, {Failed} failed",
                    result.ProcessedInstrumentCount,
                    result.SuccessfulInstrumentCount,
                    result.FailedInstrumentCount);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Historical market data sync failed");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
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
    {
        var normalized = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(normalized, TaiwanTimeZone);
        var nextDate = localNow.Date;
        if (localNow.TimeOfDay >= new TimeSpan(23, 30, 0))
            nextDate = nextDate.AddDays(1);
        while (nextDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            nextDate = nextDate.AddDays(1);

        var localNext = DateTime.SpecifyKind(nextDate.AddHours(23.5), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localNext, TaiwanTimeZone);
    }
}
