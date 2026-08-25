using System.Net;
using System.Text;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using MyExpenses.Api.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace MyExpenses.Api.Tests.Services;

public sealed class YahooHistoricalAdjustedPriceProviderTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>初始化可記錄 live smoke 結果的 provider 測試。</summary>
    public YahooHistoricalAdjustedPriceProviderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>驗證上市市場映射、adjclose 選擇及台灣交易日期轉換。</summary>
    [Fact]
    public async Task GetPricesAsync_MapsTwseAndUsesPositiveAdjustedCloseValues()
    {
        var handler = new FixtureHttpHandler(_ => YahooChartFixtures.ListedResponse());
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions { MaxRetries = 0 });

        var result = await provider.GetPricesAsync(
            StockMarket.Twse,
            " 2330 ",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7));

        Assert.Equal("YahooChart", result.Provider);
        Assert.Equal("2330.TW", result.ResolvedSymbol);
        Assert.Equal("TWD", result.Currency);
        Assert.Equal(new[] { 100m, 105m }, result.Prices.Select(point => point.AdjustedClose));
        Assert.Equal(new[] { 99m, 104m }, result.Prices.Select(point => point.Close));
        Assert.Equal(new DateOnly(2026, 8, 3), result.Prices[0].TradingDate);
        Assert.Contains("2330.TW", handler.RequestUris.Single());
        Assert.DoesNotContain("adjclose", handler.RequestUris.Single(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>驗證上櫃市場使用 TWO 後仍只保存還原價格。</summary>
    [Fact]
    public async Task GetPricesAsync_MapsTpexToTwoAndPreservesAdjustedValues()
    {
        var handler = new FixtureHttpHandler(_ => YahooChartFixtures.OverTheCounterResponse());
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions { MaxRetries = 0 });

        var result = await provider.GetPricesAsync(
            StockMarket.Tpex,
            "00679b",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7));

        Assert.Equal("00679B.TWO", result.ResolvedSymbol);
        Assert.Equal(new[] { 50m, 25m, 26m }, result.Prices.Select(point => point.AdjustedClose));
        Assert.Equal(new[] { 100m, 50m, 52m }, result.Prices.Select(point => point.Close));
        Assert.DoesNotContain(100m, result.Prices.Select(point => point.AdjustedClose));
        Assert.Contains("00679B.TWO", handler.RequestUris.Single());
    }

    /// <summary>驗證 response identity 不符時不會產生有效行情。</summary>
    [Fact]
    public async Task GetPricesAsync_RejectsWrongMarketIdentity()
    {
        var handler = new FixtureHttpHandler(_ => YahooChartFixtures.ListedResponse().Replace("2330.TW", "2330.TWO", StringComparison.Ordinal));
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions { MaxRetries = 0 });

        var exception = await Assert.ThrowsAsync<HistoricalPriceProviderException>(() => provider.GetPricesAsync(
            StockMarket.Twse,
            "2330",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7)));

        Assert.Equal("invalid_identity", exception.Code);
    }

    /// <summary>驗證 error response、非 TWD 與陣列錯位都只回傳安全錯誤。</summary>
    [Theory]
    [InlineData("error")]
    [InlineData("currency")]
    [InlineData("arrays")]
    public async Task GetPricesAsync_RejectsInvalidResponsesWithoutReturningPrices(string kind)
    {
        var handler = new FixtureHttpHandler(_ => kind switch
        {
            "error" => YahooChartFixtures.ErrorResponse(),
            "currency" => YahooChartFixtures.ListedResponse().Replace("TWD", "USD", StringComparison.Ordinal),
            _ => YahooChartFixtures.MismatchedArrayResponse(),
        });
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions { MaxRetries = 0 });

        var exception = await Assert.ThrowsAsync<HistoricalPriceProviderException>(() => provider.GetPricesAsync(
            StockMarket.Twse,
            "2330",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7)));

        Assert.NotEmpty(exception.Code);
        Assert.DoesNotContain("fixture", exception.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>驗證 provider JSON shape 不完整時統一轉為 bounded invalid response。</summary>
    [Theory]
    [InlineData("{\"chart\":null}")]
    [InlineData("{\"chart\":{\"error\":null,\"result\":[null]}}")]
    public async Task GetPricesAsync_RejectsMalformedJsonShapes(string payload)
    {
        var handler = new FixtureHttpHandler(_ => payload);
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions
        {
            MaxRetries = 0,
        });

        var exception = await Assert.ThrowsAsync<HistoricalPriceProviderException>(() => provider.GetPricesAsync(
            StockMarket.Twse,
            "2330",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7)));

        Assert.Equal("invalid_response", exception.Code);
    }

    /// <summary>驗證必要 raw close 缺值時不會以 adjusted close 偽造成功價格點。</summary>
    [Fact]
    public async Task GetPricesAsync_RejectsMissingRawClosePoint()
    {
        var handler = new FixtureHttpHandler(_ => YahooChartFixtures.MissingCloseResponse());
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions
        {
            MaxRetries = 0,
        });

        var exception = await Assert.ThrowsAsync<HistoricalPriceProviderException>(() => provider.GetPricesAsync(
            StockMarket.Twse,
            "2330",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7)));

        Assert.Equal("no_data", exception.Code);
    }

    /// <summary>驗證 HTTP 錯誤、timeout 與過大 response 都不會形成成功結果。</summary>
    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("large")]
    public async Task GetPricesAsync_UsesBoundedSafeFailureModes(string kind)
    {
        var handler = new FixtureHttpHandler(kind switch
        {
            "http" => _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
            "timeout" => async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            _ => _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(YahooChartFixtures.LargeResponse(4096), Encoding.UTF8, "application/json"),
            }),
        });
        using var client = CreateClient(handler);
        var options = new HistoricalMarketDataOptions
        {
            MaxRetries = 0,
            MaxResponseBytes = kind == "large" ? 1024 : 1_048_576,
            Timeout = kind == "timeout" ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(2),
        };
        var provider = new YahooHistoricalAdjustedPriceProvider(client, options);

        var exception = await Assert.ThrowsAsync<HistoricalPriceProviderException>(() => provider.GetPricesAsync(
            StockMarket.Twse,
            "2330",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7)));

        Assert.NotEmpty(exception.Code);
        Assert.DoesNotContain("BadGateway", exception.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>驗證非 transient 的 HTTP 4xx 會分類為永久拒絕且不進行 request retry。</summary>
    [Fact]
    public async Task GetPricesAsync_ClassifiesHttpClientErrorAsPermanentRejection()
    {
        var handler = new FixtureHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions
        {
            MaxRetries = 2,
        });

        var exception = await Assert.ThrowsAsync<HistoricalPriceProviderException>(() => provider.GetPricesAsync(
            StockMarket.Twse,
            "2330",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7)));

        Assert.Equal("http_rejected", exception.Code);
        Assert.Single(handler.RequestUris);
    }

    /// <summary>驗證歷史 provider 對 redirect 回應保留 bounded redirect failure。</summary>
    [Fact]
    public async Task GetPricesAsync_ClassifiesRedirectAsPermanentFailure()
    {
        var handler = new FixtureHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.test/redirect");
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions
        {
            MaxRetries = 0,
        });

        var exception = await Assert.ThrowsAsync<HistoricalPriceProviderException>(() => provider.GetPricesAsync(
            StockMarket.Twse,
            "2330",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7)));

        Assert.Equal("unexpected_redirect", exception.Code);
        Assert.Single(handler.RequestUris);
    }

    /// <summary>驗證 response stream IOException 會依上限重試並轉成 bounded network failure。</summary>
    [Fact]
    public async Task GetPricesAsync_RetriesStreamIOExceptionAndReturnsNetworkError()
    {
        var handler = new FixtureHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingStream()),
        }));
        using var client = CreateClient(handler);
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions
        {
            MaxRetries = 1,
        });

        var exception = await Assert.ThrowsAsync<HistoricalPriceProviderException>(() => provider.GetPricesAsync(
            StockMarket.Twse,
            "2330",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 7)));

        Assert.Equal("network_error", exception.Code);
        Assert.Equal("歷史行情服務連線失敗", exception.SafeMessage);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.DoesNotContain("fixture", exception.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只有明確設定環境變數時才呼叫 Yahoo no-key live endpoint 作為 smoke check。</summary>
    [Fact]
    public async Task LiveSmokeCheck_IsOptInAndReportsProviderLimits()
    {
        if (Environment.GetEnvironmentVariable("YAHOO_PROVIDER_SMOKE") != "1")
            return;

        using var client = new HttpClient
        {
            BaseAddress = new Uri("https://query1.finance.yahoo.com/"),
        };
        var provider = new YahooHistoricalAdjustedPriceProvider(client, new HistoricalMarketDataOptions
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxRetries = 0,
        });
        var startDate = new DateOnly(2026, 7, 1);
        var endDate = new DateOnly(2026, 8, 7);
        var result = await provider.GetPricesAsync(StockMarket.Twse, "2330", startDate, endDate);

        _output.WriteLine($"source=Yahoo Chart no-key; symbol={result.ResolvedSymbol}; currency={result.Currency}; points={result.Prices.Count}");
        _output.WriteLine("limits=unofficial endpoint, no SLA, bounded response, 10s timeout, zero retry in smoke check");
        _output.WriteLine("replacement=implement IHistoricalAdjustedPriceProvider with the same validated contract");
        Assert.NotEmpty(result.Prices);
    }

    /// <summary>建立帶有 fixture handler 的 HTTP client。</summary>
    private static HttpClient CreateClient(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("https://query1.finance.yahoo.com/") };

    /// <summary>提供固定 JSON 或錯誤的 HTTP message handler。</summary>
    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        /// <summary>初始化指定回應工廠的 fixture handler。</summary>
        public FixtureHttpHandler(Func<CancellationToken, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        /// <summary>初始化固定 JSON body 的 fixture handler。</summary>
        public FixtureHttpHandler(Func<CancellationToken, string> bodyFactory)
            : this(cancellationToken => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bodyFactory(cancellationToken), Encoding.UTF8, "application/json"),
            }))
        {
        }

        /// <summary>記錄 request URL 並建立 deterministic HTTP response。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            return await _responseFactory(cancellationToken);
        }

        /// <summary>保存測試期間收到的 request URL。</summary>
        public List<string> RequestUris { get; } = [];

    }

    /// <summary>提供讀取 response body 時拋出 IOException 的測試 stream。</summary>
    private sealed class ThrowingStream : MemoryStream
    {
        /// <summary>模擬 response body stream 的網路讀取失敗。</summary>
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => throw new IOException("fixture stream failure");
    }
}
