namespace MyExpenses.Api.Services;

using MyExpenses.Api.Models;

/// <summary>在台灣市場平日 23:00 觸發目前價格雙市場同步的背景服務。</summary>
public sealed class StockPriceUpdateService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StockPriceUpdateService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>初始化共用排程 calculator、runner 與時間來源。</summary>
    public StockPriceUpdateService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILogger<StockPriceUpdateService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _ = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>等待固定市場 slot 並委派單一 execution 給共用 runner。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Stock price update service started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = UtcNow();
                var scheduledForUtc = CalculateNextUpdateUtc(nowUtc);
                var delay = scheduledForUtc - nowUtc;
                _logger.LogInformation(
                    "Next stock price update scheduled in {Delay}",
                    FormatDelay(delay));
                await Task.Delay(delay, _timeProvider, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;

                await RunScheduledExecutionAsync(scheduledForUtc, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error during stock price update schedule loop");
                await Task.Delay(TimeSpan.FromMinutes(1), _timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>計算距離下次目前價格排程的 UTC delay。</summary>
    public static TimeSpan CalculateDelayToNextUpdate(DateTime utcNow)
    {
        var normalizedNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return CalculateNextUpdateUtc(normalizedNow) - normalizedNow;
    }

    /// <summary>計算下一個台灣平日 23:00 的 UTC slot。</summary>
    public static DateTime CalculateNextUpdateUtc(DateTime utcNow)
        => BusinessScheduleCalculator.CalculateStockPriceNextRunUtc(utcNow);

    /// <summary>以總小時數格式化排程等待時間，避免超過一天時截斷。</summary>
    public static string FormatDelay(TimeSpan delay)
        => $"{(int)Math.Max(0, delay.TotalHours)}h {Math.Max(0, delay.Minutes)}m";

    /// <summary>建立指定 slot 的 scope 並執行目前價格 typed workflow。</summary>
    private async Task RunScheduledExecutionAsync(
        DateTime scheduledForUtc,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduledJobRunner>();
        var workflow = scope.ServiceProvider.GetRequiredService<CurrentStockPriceWorkflow>();
        var localDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(scheduledForUtc, DateTimeKind.Utc),
                BusinessScheduleCalculator.TaiwanTimeZone));
        await runner.RunAsync(
            ScheduledJobKey.StockPriceUpdate,
            scheduledForUtc,
            BusinessScheduleCalculator.TaiwanTimeZone.Id,
            localDate,
            (context, token) => workflow.RunAsync(token, context.FrozenTargetKeys),
            cancellationToken);
    }

    /// <summary>取得明確 UTC 現在時間。</summary>
    private DateTime UtcNow()
        => DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
}
