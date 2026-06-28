using System.Collections.Concurrent;
using System.Text.Json;
using MyExpenses.Api.Data;

namespace MyExpenses.Api.Endpoints;

/// <summary>
/// 匯率 API 代理端點，提供即時匯率查詢與快取功能。
/// </summary>
public static class ExchangeRateEndpoints
{
    private static readonly ConcurrentDictionary<string, (DateTime Timestamp, Dictionary<string, decimal> Rates)> _rateCache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim _fetchLock = new(1, 1);
    private static readonly string[] TargetCurrencies = ["USD", "JPY", "CNY", "HKD", "TWD"];

    /// <summary>
    /// 註冊匯率相關端點。
    /// </summary>
    public static void MapExchangeRateEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/exchange-rates");

        group.MapGet("/", async (IHttpClientFactory httpFactory) =>
        {
            var cacheKey = "TWD";
            
            if (_rateCache.TryGetValue(cacheKey, out var cached) && 
                DateTime.UtcNow - cached.Timestamp < CacheDuration)
            {
                return Results.Ok(new
                {
                    @base = "TWD",
                    rates = cached.Rates,
                    updatedAt = cached.Timestamp
                });
            }

            await _fetchLock.WaitAsync();
            try
            {
                if (_rateCache.TryGetValue(cacheKey, out var doubleCheck) && 
                    DateTime.UtcNow - doubleCheck.Timestamp < CacheDuration)
                {
                    return Results.Ok(new
                    {
                        @base = "TWD",
                        rates = doubleCheck.Rates,
                        updatedAt = doubleCheck.Timestamp
                    });
                }

                var http = httpFactory.CreateClient();
                var response = await http.GetStringAsync("https://open.er-api.com/v6/latest/USD");
                
                using var doc = JsonDocument.Parse(response);
                var usdRates = new Dictionary<string, decimal>();

                if (doc.RootElement.TryGetProperty("rates", out var ratesElement))
                {
                    foreach (var prop in ratesElement.EnumerateObject())
                    {
                        if (Array.IndexOf(TargetCurrencies, prop.Name) >= 0 &&
                            decimal.TryParse(prop.Value.GetRawText(), out var rate))
                        {
                            usdRates[prop.Name] = rate;
                        }
                    }
                }

                if (!usdRates.TryGetValue("TWD", out var usdToTwd) || usdToTwd <= 0)
                {
                    return Results.Problem(
                        detail: "無法取得 TWD 匯率資料",
                        statusCode: 502);
                }

                var rates = new Dictionary<string, decimal>
                {
                    ["TWD"] = 1m,
                    ["USD"] = Math.Round(1m / usdToTwd, 6),
                };

                foreach (var currency in TargetCurrencies)
                {
                    if (currency == "TWD" || currency == "USD") continue;
                    if (usdRates.TryGetValue(currency, out var usdRate) && usdRate > 0)
                    {
                        rates[currency] = Math.Round(usdRate / usdToTwd, 6);
                    }
                }

                var timestamp = DateTime.UtcNow;
                _rateCache[cacheKey] = (timestamp, rates);

                return Results.Ok(new
                {
                    @base = "TWD",
                    rates,
                    updatedAt = timestamp
                });
            }
            catch (Exception)
            {
                if (_rateCache.TryGetValue(cacheKey, out var staleCache))
                {
                    return Results.Ok(new
                    {
                        @base = "TWD",
                        rates = staleCache.Rates,
                        updatedAt = staleCache.Timestamp,
                        warning = "使用過期快取資料"
                    });
                }

                return Results.Problem(
                    detail: "無法獲取匯率資料，請稍後再試",
                    statusCode: 503);
            }
            finally
            {
                _fetchLock.Release();
            }
        });
    }
}
