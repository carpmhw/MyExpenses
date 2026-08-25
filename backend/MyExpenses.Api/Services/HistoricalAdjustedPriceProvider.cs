using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>定義可替換的歷史還原價格 provider contract。</summary>
public interface IHistoricalAdjustedPriceProvider
{
    /// <summary>取得指定市場、代號與日期區間的還原價格。</summary>
    Task<HistoricalPriceProviderResult> GetPricesAsync(
        StockMarket market,
        string symbol,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
}

/// <summary>描述 provider 回傳的單一交易日還原與原始收盤價格。</summary>
public sealed record HistoricalPricePoint(DateOnly TradingDate, decimal AdjustedClose, decimal Close);

/// <summary>描述 provider 驗證後的行情結果與供應商身分。</summary>
public sealed record HistoricalPriceProviderResult(
    string Provider,
    string ResolvedSymbol,
    string ExchangeName,
    string Currency,
    IReadOnlyList<HistoricalPricePoint> Prices);

/// <summary>集中管理歷史行情 adapter 的 timeout、回應大小與 retry 上限。</summary>
public sealed class HistoricalMarketDataOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxResponseBytes { get; set; } = 1_048_576;
    public int MaxRetries { get; set; } = 2;
    public int HistoryMonths { get; set; } = 60;

    /// <summary>驗證歷史行情期間維持在可控的 1 到 60 個月範圍。</summary>
    public void Validate()
    {
        if (HistoryMonths is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(HistoryMonths), "歷史行情期間必須介於 1 到 60 個月");
    }
}

/// <summary>表示不宜將 provider 內部例外直接暴露給使用者的安全錯誤。</summary>
public sealed class HistoricalPriceProviderException : Exception
{
    /// <summary>建立帶有穩定錯誤代碼與安全訊息的 provider 例外。</summary>
    public HistoricalPriceProviderException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
        SafeMessage = safeMessage;
    }

    public string Code { get; }
    public string SafeMessage { get; }
}

/// <summary>以 Yahoo Chart JSON 取得台灣上市與上櫃還原權息價格。</summary>
public sealed class YahooHistoricalAdjustedPriceProvider : IHistoricalAdjustedPriceProvider
{
    private const string ProviderName = "YahooChart";
    private const string ExpectedCurrency = "TWD";
    private static readonly TimeZoneInfo TaiwanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private readonly HttpClient _httpClient;
    private readonly HistoricalMarketDataOptions _options;

    /// <summary>初始化使用指定 HTTP client 與安全傳輸限制的 Yahoo adapter。</summary>
    public YahooHistoricalAdjustedPriceProvider(
        HttpClient httpClient,
        HistoricalMarketDataOptions? options = null)
    {
        _httpClient = httpClient;
        _options = options ?? new HistoricalMarketDataOptions();
        _options.Validate();
        if (_options.Timeout <= TimeSpan.Zero)
            _options.Timeout = TimeSpan.FromSeconds(15);
        if (_options.MaxResponseBytes < 1024)
            _options.MaxResponseBytes = 1024;
        if (_options.MaxRetries < 0)
            _options.MaxRetries = 0;

        _httpClient.Timeout = _options.Timeout;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("MyExpenses", "1.0"));
    }

    /// <summary>依交易市場映射 Yahoo suffix 並回傳已驗證的雙價格點。</summary>
    public async Task<HistoricalPriceProviderResult> GetPricesAsync(
        StockMarket market,
        string symbol,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (market is not (StockMarket.Twse or StockMarket.Tpex))
            throw new HistoricalPriceProviderException("invalid_market", "歷史行情市場尚未辨識");
        if (endDate < startDate)
            throw new HistoricalPriceProviderException("invalid_range", "歷史行情日期區間無效");

        var suffix = market == StockMarket.Twse ? ".TW" : ".TWO";
        var resolvedSymbol = normalizedSymbol + suffix;
        var uri = BuildChartUri(resolvedSymbol, startDate, endDate);
        var payload = await GetPayloadAsync(uri, cancellationToken);
        return ParsePayload(payload, resolvedSymbol, startDate, endDate);
    }

    /// <summary>提供語意化別名，供同步協調器以 fetch 語意呼叫 adapter。</summary>
    public Task<HistoricalPriceProviderResult> FetchAsync(
        StockMarket market,
        string symbol,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
        => GetPricesAsync(market, symbol, startDate, endDate, cancellationToken);

    /// <summary>正規化且驗證 provider 請求使用的股票代號。</summary>
    private static string NormalizeSymbol(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Length == 0 || normalized.Length > 20 || normalized.Contains('.'))
            throw new HistoricalPriceProviderException("invalid_symbol", "股票代號格式無效");
        return normalized;
    }

    /// <summary>建立不含密鑰且包含日期範圍的 Yahoo Chart request URI。</summary>
    private static Uri BuildChartUri(string resolvedSymbol, DateOnly startDate, DateOnly endDate)
    {
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
            TaiwanTimeZone);
        var endExclusiveUtc = TimeZoneInfo.ConvertTimeToUtc(
            endDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
            TaiwanTimeZone);
        var query = string.Join('&',
            $"period1={new DateTimeOffset(startUtc).ToUnixTimeSeconds()}",
            $"period2={new DateTimeOffset(endExclusiveUtc).ToUnixTimeSeconds()}",
            "interval=1d",
            "events=div%2Csplits",
            "includeAdjustedClose=true");
        return new Uri($"v8/finance/chart/{Uri.EscapeDataString(resolvedSymbol)}?{query}", UriKind.Relative);
    }

    /// <summary>以 bounded stream 讀取 provider 回應並對 transient failure 做有限次重試。</summary>
    private async Task<string> GetPayloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < _options.MaxRetries)
                    {
                        await DelayBeforeRetryAsync(attempt, cancellationToken);
                        continue;
                    }

                    throw new HistoricalPriceProviderException(
                        GetHttpFailureCode(response.StatusCode),
                        GetHttpFailureMessage(response.StatusCode));
                }

                if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
                    throw new HistoricalPriceProviderException("response_too_large", "歷史行情回應超過安全大小限制");

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await ReadBoundedTextAsync(stream, cancellationToken);
            }
            catch (HistoricalPriceProviderException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < _options.MaxRetries)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                throw new HistoricalPriceProviderException("timeout", "歷史行情服務逾時");
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new HistoricalPriceProviderException("network_error", "歷史行情服務連線失敗");
            }
            catch (IOException) when (attempt < _options.MaxRetries)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (IOException)
            {
                throw new HistoricalPriceProviderException("network_error", "歷史行情服務連線失敗");
            }
        }

        throw new HistoricalPriceProviderException("network_error", "歷史行情服務連線失敗");
    }

    /// <summary>判斷 HTTP status 是否適合進行 bounded transient retry。</summary>
    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or (HttpStatusCode)429
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            || (int)statusCode >= 500;

    /// <summary>將 HTTP status 映射為 transient、redirect 或永久拒絕代碼。</summary>
    private static string GetHttpFailureCode(HttpStatusCode statusCode)
        => IsTransient(statusCode)
            ? "http_error"
            : (int)statusCode is >= 300 and < 400
                ? "unexpected_redirect"
                : "http_rejected";

    /// <summary>建立不含 status 原文與 response body 的安全 HTTP 訊息。</summary>
    private static string GetHttpFailureMessage(HttpStatusCode statusCode)
        => (int)statusCode is >= 300 and < 400
            ? "歷史行情服務回應不受支援的重新導向"
            : IsTransient(statusCode)
                ? "歷史行情服務暫時無法使用"
                : "歷史行情服務拒絕請求";

    /// <summary>以短暫且固定上限的退避延遲避免連續轟擊公開 provider。</summary>
    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);

    /// <summary>以固定 buffer 讀取 response，超過上限即停止並回傳安全錯誤。</summary>
    private async Task<string> ReadBoundedTextAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > _options.MaxResponseBytes)
                throw new HistoricalPriceProviderException("response_too_large", "歷史行情回應超過安全大小限制");
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>驗證 Yahoo identity 與雙價格序列後解析正值交易日點。</summary>
    private static HistoricalPriceProviderResult ParsePayload(
        string payload,
        string expectedSymbol,
        DateOnly startDate,
        DateOnly endDate)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var chart = document.RootElement.GetProperty("chart");
            var error = chart.GetProperty("error");
            if (error.ValueKind is not JsonValueKind.Null)
                throw new HistoricalPriceProviderException("provider_error", "歷史行情服務回傳錯誤");

            var result = chart.GetProperty("result");
            if (result.ValueKind is not JsonValueKind.Array || result.GetArrayLength() == 0)
                throw new HistoricalPriceProviderException("no_data", "歷史行情沒有可用資料");

            var item = result[0];
            var meta = item.GetProperty("meta");
            var actualSymbol = meta.GetProperty("symbol").GetString()?.Trim().ToUpperInvariant();
            var exchangeName = meta.GetProperty("exchangeName").GetString()?.Trim() ?? string.Empty;
            var currency = meta.GetProperty("currency").GetString()?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!string.Equals(actualSymbol, expectedSymbol, StringComparison.OrdinalIgnoreCase)
                || exchangeName.Length == 0)
                throw new HistoricalPriceProviderException("invalid_identity", "歷史行情標的身分無法驗證");
            if (!string.Equals(currency, ExpectedCurrency, StringComparison.Ordinal))
                throw new HistoricalPriceProviderException("invalid_currency", "歷史行情幣別不受支援");

            var timestamps = item.GetProperty("timestamp");
            var adjusted = item
                .GetProperty("indicators")
                .GetProperty("adjclose")[0]
                .GetProperty("adjclose");
            var close = item
                .GetProperty("indicators")
                .GetProperty("quote")[0]
                .GetProperty("close");
            if (timestamps.ValueKind is not JsonValueKind.Array
                || adjusted.ValueKind is not JsonValueKind.Array
                || close.ValueKind is not JsonValueKind.Array
                || timestamps.GetArrayLength() != adjusted.GetArrayLength()
                || timestamps.GetArrayLength() != close.GetArrayLength())
                throw new HistoricalPriceProviderException("invalid_series", "歷史行情時間序列格式無效");

            var points = new Dictionary<DateOnly, (decimal AdjustedClose, decimal Close)>();
            for (var index = 0; index < timestamps.GetArrayLength(); index++)
            {
                if (!timestamps[index].TryGetInt64(out var unixSeconds)
                    || adjusted[index].ValueKind is JsonValueKind.Null
                    || !TryGetPositiveDecimal(adjusted[index], out var adjustedClose)
                    || close[index].ValueKind is JsonValueKind.Null
                    || !TryGetPositiveDecimal(close[index], out var rawClose))
                    continue;

                DateTime localDateTime;
                try
                {
                    var utc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
                    localDateTime = TimeZoneInfo.ConvertTimeFromUtc(utc, TaiwanTimeZone);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                var tradingDate = DateOnly.FromDateTime(localDateTime);
                if (tradingDate < startDate || tradingDate > endDate)
                    continue;
                points[tradingDate] = (adjustedClose, rawClose);
            }

            if (points.Count == 0)
                throw new HistoricalPriceProviderException("no_data", "歷史行情沒有可用價格");

            return new HistoricalPriceProviderResult(
                ProviderName,
                expectedSymbol,
                exchangeName,
                currency,
                points.OrderBy(item => item.Key)
                    .Select(item => new HistoricalPricePoint(
                        item.Key,
                        item.Value.AdjustedClose,
                        item.Value.Close))
                    .ToList());
        }
        catch (HistoricalPriceProviderException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new HistoricalPriceProviderException("invalid_json", "歷史行情回應格式無效");
        }
        catch (KeyNotFoundException)
        {
            throw new HistoricalPriceProviderException("invalid_response", "歷史行情回應欄位不完整");
        }
        catch (InvalidOperationException)
        {
            throw new HistoricalPriceProviderException("invalid_response", "歷史行情回應格式無效");
        }
        catch (IndexOutOfRangeException)
        {
            throw new HistoricalPriceProviderException("invalid_response", "歷史行情回應格式無效");
        }
    }

    /// <summary>解析 JSON 數值並只接受有限且正值的 decimal 價格。</summary>
    private static bool TryGetPositiveDecimal(JsonElement value, out decimal result)
    {
        result = 0m;
        if (value.ValueKind is not JsonValueKind.Number
            || !decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            return false;
        return result > 0m;
    }
}
