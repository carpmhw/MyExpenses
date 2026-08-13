using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class CurrentPriceProviderTests
{
    /// <summary>驗證 TWSE adapter 以 invariant culture 解析有效收盤價。</summary>
    [Fact]
    public async Task TwseProvider_ParsesInvariantPriceRecords()
    {
        var handler = new StubHandler(_ => JsonResponse("[{\"Code\":\"2330\",\"Name\":\"台積電\",\"ClosingPrice\":\"1,234.50\"}]"));
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client);

        var result = await provider.FetchAsync();

        Assert.Null(result.Failure);
        var record = Assert.Single(result.Records);
        Assert.Equal("2330", record.Symbol);
        Assert.Equal("台積電", record.Name);
        Assert.Equal(1234.50m, record.Price);
    }

    /// <summary>驗證 TPEx adapter 可解析其代號與收盤價欄位。</summary>
    [Fact]
    public async Task TpexProvider_ParsesTypedRecords()
    {
        var handler = new StubHandler(_ => JsonResponse("[{\"SecuritiesCompanyCode\":\"6488\",\"CompanyName\":\"環球晶\",\"ClosingPrice\":\"88.25\"}]"));
        using var client = new HttpClient(handler);
        var provider = new TpexCurrentPriceProvider(client);

        var result = await provider.FetchAsync();

        Assert.Null(result.Failure);
        Assert.Equal("6488", Assert.Single(result.Records).Symbol);
        Assert.Equal("環球晶", Assert.Single(result.Records).Name);
        Assert.Equal(88.25m, Assert.Single(result.Records).Price);
    }

    /// <summary>驗證官方清單保留代號與名稱，即使當日價格為空也不視為 parser failure。</summary>
    [Fact]
    public async Task TwseProvider_PreservesCatalogMembershipWhenPriceIsMissing()
    {
        var handler = new StubHandler(_ => JsonResponse("[{\"Code\":\"2330\",\"Name\":\"台積電\",\"ClosingPrice\":\"\"}]"));
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client);

        var result = await provider.FetchAsync();

        Assert.Null(result.Failure);
        var record = Assert.Single(result.Records);
        Assert.Equal("2330", record.Symbol);
        Assert.Equal("台積電", record.Name);
        Assert.Null(record.Price);
    }

    /// <summary>驗證 parser 允許剛好 1 MiB application bytes。</summary>
    [Fact]
    public async Task Provider_AllowsExactlyMaximumApplicationBytes()
    {
        const int maximum = 1_048_576;
        var prefix = Encoding.UTF8.GetBytes("[{\"Code\":\"2330\",\"ClosingPrice\":\"100\"}]");
        var payload = new byte[maximum];
        prefix.CopyTo(payload, 0);
        for (var index = prefix.Length; index < payload.Length; index++)
            payload[index] = (byte)' ';
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client, new CurrentPriceProviderOptions
        {
            MaxResponseBytes = maximum,
        });

        var result = await provider.FetchAsync();

        Assert.Null(result.Failure);
        Assert.Single(result.Records);
    }

    /// <summary>驗證讀取第 1,048,577 byte 時回傳 bounded response-too-large failure。</summary>
    [Fact]
    public async Task Provider_RejectsOneByteOverMaximum()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1_048_577]),
        });
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client);

        var result = await provider.FetchAsync();

        Assert.Equal("ResponseTooLarge", result.Failure?.Code);
    }

    /// <summary>驗證 array 含非 object 元素時回傳格式錯誤而不逸出 parser 例外。</summary>
    [Fact]
    public async Task Provider_RejectsMalformedArrayElement()
    {
        var handler = new StubHandler(_ => JsonResponse("[null]"));
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client);

        var result = await provider.FetchAsync();

        Assert.Equal("InvalidProviderResponse", result.Failure?.Code);
        Assert.Empty(result.Records);
    }

    /// <summary>驗證缺少 provider 代號欄位的資料列回傳格式錯誤而非空資料重試。</summary>
    [Fact]
    public async Task Provider_RejectsRecordWithoutSymbol()
    {
        var handler = new StubHandler(_ => JsonResponse("[{\"ClosingPrice\":\"100\"}]"));
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client);

        var result = await provider.FetchAsync();

        Assert.Equal("InvalidProviderResponse", result.Failure?.Code);
    }

    /// <summary>驗證 redirect 不會被自動跟隨且不保存 Location。</summary>
    [Fact]
    public async Task Provider_RejectsRedirectWithoutFollowingLocation()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri("https://example.test/?token=secret");
            return response;
        });
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client);

        var result = await provider.FetchAsync();

        Assert.Equal("UnexpectedRedirect", result.Failure?.Code);
        Assert.Equal("twse-current-price", result.Failure?.LogicalEndpoint);
        Assert.DoesNotContain("secret", result.Failure?.SafeMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>驗證呼叫端 cancellation 會直接向上傳遞而非轉成 provider failure。</summary>
    [Fact]
    public async Task Provider_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(_ => throw new OperationCanceledException(cancellation.Token));
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.FetchAsync(cancellation.Token));
    }

    /// <summary>驗證 response stream 讀取例外會轉成 bounded network failure。</summary>
    [Fact]
    public async Task Provider_MapsStreamFailureToBoundedNetworkError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingStream()),
        });
        using var client = new HttpClient(handler);
        var provider = new TwseCurrentPriceProvider(client);

        var result = await provider.FetchAsync();

        Assert.Equal("NetworkError", result.Failure?.Code);
        Assert.True(result.Failure?.Retryable);
    }

    /// <summary>建立固定 JSON HTTP response。</summary>
    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>提供可控制 response 與 cancellation 的 HTTP handler。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        /// <summary>初始化指定 response handler。</summary>
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        /// <summary>記錄 adapter 實際發出的 request 數量。</summary>
        public int CallCount { get; private set; }

        /// <summary>執行測試指定的 HTTP response。</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_handler(request));
        }
    }

    /// <summary>提供讀取時拋出 IOException 的測試 stream。</summary>
    private sealed class ThrowingStream : MemoryStream
    {
        /// <summary>模擬 response body 讀取失敗。</summary>
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => throw new IOException("fixture stream failure");
    }
}
