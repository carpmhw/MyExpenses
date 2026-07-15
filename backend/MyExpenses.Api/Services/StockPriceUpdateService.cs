using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;

namespace MyExpenses.Api.Services;

public class StockPriceUpdateService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<StockPriceUpdateService> _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly TimeZoneInfo MarketTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    /// <summary>
    /// 初始化股票價格更新服務
    /// </summary>
    public StockPriceUpdateService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILogger<StockPriceUpdateService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 背景服務主要執行迴圈，定期更新台股價格
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Stock price update service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
                var delay = CalculateDelayToNextUpdate(nowUtc);
                _logger.LogInformation("Next stock price update scheduled in {Hours}h {Minutes}m",
                    delay.Hours, delay.Minutes);

                await Task.Delay(delay, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;

                await UpdateStockPrices(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stock price update");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// 計算距離下次台股開盤更新的延遲時間（跳過週末）
    /// </summary>
    public static TimeSpan CalculateDelayToNextUpdate(DateTime utcNow)
    {
        var normalizedNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return CalculateNextUpdateUtc(normalizedNow) - normalizedNow;
    }

    /// <summary>Calculates the next 23:00 weekday update in the fixed Taiwan market time zone.</summary>
    public static DateTime CalculateNextUpdateUtc(DateTime utcNow)
    {
        var normalizedNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(normalizedNow, MarketTimeZone);
        var nextDate = localNow.Date;
        if (localNow.TimeOfDay >= TimeSpan.FromHours(15))
            nextDate = nextDate.AddDays(1);

        while (nextDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            nextDate = nextDate.AddDays(1);

        var localNext = DateTime.SpecifyKind(nextDate.AddHours(23), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localNext, MarketTimeZone);
    }

    /// <summary>
    /// 從 TWSE API 取得最新股價並更新資料庫
    /// </summary>
    private async Task UpdateStockPrices(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation("Fetching stock prices from TWSE");

        var twseData = await FetchTwseData(ct);
        if (twseData is null || twseData.Count == 0)
        {
            _logger.LogWarning("No data returned from TWSE API");
            return;
        }

        var stocks = await db.Stocks.ToListAsync(ct);
        var now = DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        var updatedCount = 0;

        foreach (var stock in stocks)
        {
            var key = stock.Symbol.Trim().ToUpperInvariant();
            if (twseData.TryGetValue(key, out var price) && price.HasValue)
            {
                stock.CurrentPrice = price.Value;
                stock.LastPriceUpdate = now;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
            await db.SaveChangesAsync(ct);

        _logger.LogInformation("Updated prices for {Count}/{Total} stocks", updatedCount, stocks.Count);
    }

    /// <summary>
    /// 呼叫 TWSE 開放 API 取得所有股票日成交資訊
    /// </summary>
    private async Task<Dictionary<string, decimal?>?> FetchTwseData(CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            var response = await http.GetStringAsync(
                "https://openapi.twse.com.tw/v1/exchangeReport/STOCK_DAY_ALL", ct);

            using var doc = JsonDocument.Parse(response);
            var result = new Dictionary<string, decimal?>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var code = item.GetProperty("Code").GetString();
                decimal? closingPrice = null;
                if (item.TryGetProperty("ClosingPrice", out var cp) && cp.ValueKind == JsonValueKind.String)
                {
                    var cpStr = cp.GetString();
                    if (decimal.TryParse(cpStr, out var parsed))
                        closingPrice = parsed;
                }
                if (code != null)
                    result[code.Trim().ToUpperInvariant()] = closingPrice;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch stock data from TWSE");
            return null;
        }
    }
}
