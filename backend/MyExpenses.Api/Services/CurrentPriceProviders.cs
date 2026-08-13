using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>保存 current-price provider 的 bounded HTTP options。</summary>
public sealed class CurrentPriceProviderOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxResponseBytes { get; set; } = 1_048_576;
    public string? Endpoint { get; set; }
}

/// <summary>描述供應商回傳的單筆已正規化市場資料。</summary>
public sealed record CurrentPriceRecord(string Symbol, decimal? Price, string? Name = null);

/// <summary>描述不含原始 response 的 typed provider failure。</summary>
public sealed record CurrentPriceProviderFailure(
    string Code,
    string SafeMessage,
    bool Retryable,
    string LogicalEndpoint = "current-price");

/// <summary>封裝 provider records 與 bounded failure 結果。</summary>
public sealed record CurrentPriceProviderResult(
    string Provider,
    IReadOnlyList<CurrentPriceRecord> Records,
    CurrentPriceProviderFailure? Failure)
{
    /// <summary>建立 provider 成功結果。</summary>
    public static CurrentPriceProviderResult Success(
        string provider,
        IReadOnlyList<CurrentPriceRecord> records)
        => new(provider, records, null);

    /// <summary>建立不含原始資料的 provider failure 結果。</summary>
    public static CurrentPriceProviderResult Failed(
        string provider,
        string code,
        string safeMessage,
        bool retryable,
        string logicalEndpoint = "current-price")
        => new(provider, [], new CurrentPriceProviderFailure(code, safeMessage, retryable, logicalEndpoint));

    /// <summary>建立 provider 成功但沒有可處理資料的結果。</summary>
    public static CurrentPriceProviderResult NoWork(string provider)
        => new(provider, [], null);
}

/// <summary>定義上市與上櫃目前價格 adapter 的共同 contract。</summary>
public interface ICurrentPriceProvider
{
    /// <summary>取得 provider 的安全名稱。</summary>
    string ProviderName { get; }

    /// <summary>取得 adapter 對應的股票市場。</summary>
    StockMarket Market { get; }

    /// <summary>發出 bounded HTTP request 並回傳 typed current-price 結果。</summary>
    Task<CurrentPriceProviderResult> FetchAsync(CancellationToken cancellationToken = default);
}

/// <summary>共用目前價格 HTTP response 限制、狀態映射與 bounded parser 基底。</summary>
public abstract class CurrentPriceProviderBase : ICurrentPriceProvider
{
    private readonly HttpClient _httpClient;
    private readonly CurrentPriceProviderOptions _options;

    /// <summary>初始化指定 HTTP client、限制與 logical endpoint。</summary>
    protected CurrentPriceProviderBase(
        HttpClient httpClient,
        CurrentPriceProviderOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new CurrentPriceProviderOptions();
        if (_options.Timeout <= TimeSpan.Zero)
            _options.Timeout = TimeSpan.FromSeconds(15);
        if (_options.MaxResponseBytes < 1)
            _options.MaxResponseBytes = 1_048_576;
        _httpClient.Timeout = _options.Timeout;
    }

    /// <summary>取得供應商安全名稱。</summary>
    public abstract string ProviderName { get; }

    /// <summary>取得 adapter 對應的股票市場。</summary>
    public abstract StockMarket Market { get; }

    /// <summary>取得預設 logical endpoint，避免將完整 URL 放進摘要或 log。</summary>
    protected abstract string DefaultEndpoint { get; }

    /// <summary>取得不含完整 URL 的安全 logical endpoint 名稱。</summary>
    protected abstract string LogicalEndpoint { get; }

    /// <summary>以 bounded HTTP response 讀取並解析供應商資料。</summary>
    public async Task<CurrentPriceProviderResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Endpoint ?? DefaultEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
                return CurrentPriceProviderResult.Failed(
                    ProviderName,
                    "UnexpectedRedirect",
                    "行情服務回傳不受支援的重新導向",
                    false,
                    LogicalEndpoint);
            if (!response.IsSuccessStatusCode)
            {
                var retryable = IsTransientStatus(response.StatusCode);
                return CurrentPriceProviderResult.Failed(
                    ProviderName,
                    retryable ? "ProviderUnavailable" : "ProviderRejected",
                    retryable ? "行情服務暫時無法使用" : "行情服務拒絕請求",
                    retryable,
                    LogicalEndpoint);
            }

            if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
                return CurrentPriceProviderResult.Failed(
                    ProviderName,
                    "ResponseTooLarge",
                    "行情服務回應超過安全大小限制",
                    false,
                    LogicalEndpoint);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await ReadBoundedBytesAsync(stream, cancellationToken);
            var records = ParseRecords(payload);
            return records.Count == 0
                ? CurrentPriceProviderResult.Failed(
                    ProviderName,
                    "ProviderUnavailable",
                    "行情服務沒有回傳資料",
                    true,
                    LogicalEndpoint)
                : CurrentPriceProviderResult.Success(ProviderName, records);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CurrentPriceProviderResult.Failed(
                ProviderName,
                "Timeout",
                "行情服務逾時",
                true,
                LogicalEndpoint);
        }
        catch (HttpRequestException)
        {
            return CurrentPriceProviderResult.Failed(
                ProviderName,
                "NetworkError",
                "行情服務連線失敗",
                true,
                LogicalEndpoint);
        }
        catch (IOException)
        {
            return CurrentPriceProviderResult.Failed(
                ProviderName,
                "NetworkError",
                "行情服務回應讀取失敗",
                true,
                LogicalEndpoint);
        }
        catch (CurrentPriceProviderParsingException exception)
        {
            return CurrentPriceProviderResult.Failed(
                ProviderName,
                exception.Code,
                exception.SafeMessage,
                false,
                LogicalEndpoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CurrentPriceProviderResult.Failed(
                ProviderName,
                "ProviderFailure",
                "行情服務回應無法處理",
                false,
                LogicalEndpoint);
        }
    }

    /// <summary>由子類別將 bounded JSON bytes 轉成正規化 records。</summary>
    protected abstract IReadOnlyList<CurrentPriceRecord> ParseRecords(ReadOnlyMemory<byte> payload);

    /// <summary>以 application bytes 上限讀取 response stream。</summary>
    private async Task<ReadOnlyMemory<byte>> ReadBoundedBytesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > _options.MaxResponseBytes)
                throw new CurrentPriceProviderParsingException(
                    "ResponseTooLarge",
                    "行情服務回應超過安全大小限制");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    /// <summary>判斷 HTTP status 是否可在 runner 中重試。</summary>
    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or (HttpStatusCode)429
            || (int)statusCode >= 500;

    /// <summary>解析 provider 的 bounded 正數價格欄位。</summary>
    protected static decimal? ParsePrice(JsonElement item, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!item.TryGetProperty(propertyName, out var value))
                continue;
            var raw = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
            if (decimal.TryParse(
                    raw,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var price)
                && price > 0m)
                return price;
            return null;
        }

        return null;
    }

    /// <summary>解析 provider 代號欄位並移除外部空白。</summary>
    protected static string ParseSymbol(JsonElement item, params string[] propertyNames)
    {
        if (item.ValueKind is not JsonValueKind.Object)
            throw new CurrentPriceProviderParsingException(
                "InvalidProviderResponse",
                "行情服務回應格式無效");

        foreach (var propertyName in propertyNames)
        {
            if (!item.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String)
                continue;
            var symbol = value.GetString()?.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(symbol) && symbol.Length <= 20)
                return symbol;
        }

        throw new CurrentPriceProviderParsingException(
            "InvalidProviderResponse",
            "行情服務回應缺少股票代號");
    }

    /// <summary>解析 provider 名稱欄位，缺少名稱時保留 nullable 結果。</summary>
    protected static string? ParseName(JsonElement item, params string[] propertyNames)
    {
        if (item.ValueKind is not JsonValueKind.Object)
            throw new CurrentPriceProviderParsingException(
                "InvalidProviderResponse",
                "行情服務回應格式無效");

        foreach (var propertyName in propertyNames)
        {
            if (!item.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String)
                continue;
            var name = value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && name.Length <= 200)
                return name;
        }

        return null;
    }

    /// <summary>解析 JSON array 並將格式錯誤轉成安全 parser exception。</summary>
    protected static JsonElement RequireArray(ReadOnlyMemory<byte> payload, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                document.Dispose();
                throw new CurrentPriceProviderParsingException(
                    "InvalidProviderResponse",
                    "行情服務回應格式無效");
            }

            return document.RootElement;
        }
        catch (JsonException)
        {
            throw new CurrentPriceProviderParsingException(
                "InvalidProviderResponse",
                "行情服務回應格式無效");
        }
    }
}

/// <summary>表示 parser 不宜向 workflow 暴露的 bounded 格式錯誤。</summary>
public sealed class CurrentPriceProviderParsingException : Exception
{
    /// <summary>建立安全 parser failure。</summary>
    public CurrentPriceProviderParsingException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
        SafeMessage = safeMessage;
    }

    /// <summary>取得穩定 machine-readable code。</summary>
    public string Code { get; }

    /// <summary>取得不包含 payload 的安全訊息。</summary>
    public string SafeMessage { get; }
}

/// <summary>解析 TWSE STOCK_DAY_ALL current-price response。</summary>
public sealed class TwseCurrentPriceProvider : CurrentPriceProviderBase
{
    /// <summary>初始化 TWSE adapter。</summary>
    public TwseCurrentPriceProvider(HttpClient httpClient, CurrentPriceProviderOptions? options = null)
        : base(httpClient, options)
    {
    }

    /// <summary>取得 TWSE provider 名稱。</summary>
    public override string ProviderName => "TWSE";

    /// <summary>取得 TWSE 市場。</summary>
    public override StockMarket Market => StockMarket.Twse;

    /// <summary>取得 TWSE logical endpoint。</summary>
    protected override string DefaultEndpoint => "https://openapi.twse.com.tw/v1/exchangeReport/STOCK_DAY_ALL";

    /// <summary>取得 TWSE 安全 logical endpoint 名稱。</summary>
    protected override string LogicalEndpoint => "twse-current-price";

    /// <summary>解析 TWSE 代號與收盤價欄位。</summary>
    protected override IReadOnlyList<CurrentPriceRecord> ParseRecords(ReadOnlyMemory<byte> payload)
    {
        var root = RequireArray(payload, out var document);
        using (document)
        {
            return root.EnumerateArray()
                .Select(item => new CurrentPriceRecord(
                    ParseSymbol(item, "Code") ?? string.Empty,
                    ParsePrice(item, "ClosingPrice"),
                    ParseName(item, "Name")))
                .Where(record => record.Symbol.Length > 0)
                .ToList();
        }
    }
}

/// <summary>解析 TPEx current-price response。</summary>
public sealed class TpexCurrentPriceProvider : CurrentPriceProviderBase
{
    /// <summary>初始化 TPEx adapter。</summary>
    public TpexCurrentPriceProvider(HttpClient httpClient, CurrentPriceProviderOptions? options = null)
        : base(httpClient, options)
    {
    }

    /// <summary>取得 TPEx provider 名稱。</summary>
    public override string ProviderName => "TPEx";

    /// <summary>取得 TPEx 市場。</summary>
    public override StockMarket Market => StockMarket.Tpex;

    /// <summary>取得 TPEx logical endpoint。</summary>
    protected override string DefaultEndpoint => "https://www.tpex.org.tw/openapi/v1/tpex_mainboard_quotes";

    /// <summary>取得 TPEx 安全 logical endpoint 名稱。</summary>
    protected override string LogicalEndpoint => "tpex-current-price";

    /// <summary>解析 TPEx 常見代號與收盤價欄位。</summary>
    protected override IReadOnlyList<CurrentPriceRecord> ParseRecords(ReadOnlyMemory<byte> payload)
    {
        var root = RequireArray(payload, out var document);
        using (document)
        {
            return root.EnumerateArray()
                .Select(item => new CurrentPriceRecord(
                    ParseSymbol(item, "SecuritiesCompanyCode", "Code", "證券代號") ?? string.Empty,
                    ParsePrice(item, "ClosingPrice", "Close", "ClosePrice", "收盤價"),
                    ParseName(item, "CompanyName", "Name", "公司名稱")))
                .Where(record => record.Symbol.Length > 0)
                .ToList();
        }
    }
}
