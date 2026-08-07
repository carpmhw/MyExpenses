using System.Text.Json;

namespace MyExpenses.Api.Tests.Fixtures;

/// <summary>建立去識別化 Yahoo Chart 回應，避免 provider 測試依賴 live endpoint。</summary>
public static class YahooChartFixtures
{
    /// <summary>建立上市標的含 null 與負值還原價格的有效回應。</summary>
    public static string ListedResponse()
        => CreateResponse("2330.TW", "TAI", "TWD", new decimal?[] { 100m, null, -2m, 105m }, new decimal?[] { 99m, 101m, 102m, 104m });

    /// <summary>建立上櫃標的含除權息調整差異的有效回應。</summary>
    public static string OverTheCounterResponse()
        => CreateResponse("00679B.TWO", "TAI", "TWD", new decimal?[] { 50m, 25m, 26m }, new decimal?[] { 100m, 50m, 52m });

    /// <summary>建立 provider 回傳錯誤代號的回應。</summary>
    public static string ErrorResponse()
        => "{\"chart\":{\"result\":null,\"error\":{\"code\":\"Bad Request\",\"description\":\"fixture failure\"}}}";

    /// <summary>建立時間戳與還原價格陣列長度不一致的回應。</summary>
    public static string MismatchedArrayResponse()
        => CreateResponse("2330.TW", "TAI", "TWD", new decimal?[] { 100m, 101m }, new decimal?[] { 99m });

    /// <summary>建立指定回應大小的有效 JSON，供 bounded response 測試使用。</summary>
    public static string LargeResponse(int targetBytes)
    {
        var padding = new string('x', Math.Max(0, targetBytes));
        return $"{{\"chart\":{{\"result\":[],\"error\":null,\"padding\":\"{padding}\"}}}}";
    }

    /// <summary>將測試日期轉成 Yahoo Chart 使用的 Unix timestamp。</summary>
    public static long Timestamp(DateTime dateUtc)
        => new DateTimeOffset(DateTime.SpecifyKind(dateUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>建立最小 Yahoo Chart JSON 結構並保留 adjclose 與 close 的差異。</summary>
    private static string CreateResponse(
        string symbol,
        string exchange,
        string currency,
        IReadOnlyList<decimal?> adjustedCloses,
        IReadOnlyList<decimal?> closes)
    {
        var timestamps = new[]
        {
            Timestamp(new DateTime(2026, 8, 3, 0, 0, 0)),
            Timestamp(new DateTime(2026, 8, 4, 0, 0, 0)),
            Timestamp(new DateTime(2026, 8, 5, 0, 0, 0)),
            Timestamp(new DateTime(2026, 8, 6, 0, 0, 0)),
        };
        var chartTimestamps = timestamps.Take(adjustedCloses.Count).ToArray();
        var result = new
        {
            chart = new
            {
                result = new[]
                {
                    new
                    {
                        meta = new { symbol, exchangeName = exchange, currency },
                        timestamp = chartTimestamps,
                        indicators = new
                        {
                            quote = new[] { new { close = closes } },
                            adjclose = new[] { new { adjclose = adjustedCloses } },
                        },
                    },
                },
                error = (object?)null,
            },
        };

        return JsonSerializer.Serialize(result);
    }
}
