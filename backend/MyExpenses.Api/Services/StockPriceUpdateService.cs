using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;

namespace MyExpenses.Api.Services;

public class StockPriceUpdateService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<StockPriceUpdateService> _logger;
    private readonly TimeZoneService _timeZoneService;

    /// <summary>
    /// 初始化股票價格更新服務
    /// </summary>
    public StockPriceUpdateService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILogger<StockPriceUpdateService> logger,
        TimeZoneService timeZoneService)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _logger = logger;
        _timeZoneService = timeZoneService;
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
                var delay = CalculateDelayToNextUpdate();
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
    private TimeSpan CalculateDelayToNextUpdate()
    {
        var now = DateTime.UtcNow;
        var next = now.Date.AddHours(15);
        if (now >= next)
            next = next.AddDays(1);

        var localNext = _timeZoneService.ConvertUtcToLocal(next);

        while (localNext.DayOfWeek == DayOfWeek.Saturday || localNext.DayOfWeek == DayOfWeek.Sunday)
            localNext = localNext.AddDays(1);

        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(localNext, _timeZoneService.GetTimeZoneInfo());
        return nextUtc - now;
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
        var now = DateTime.UtcNow;
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
