using System.Net;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class ExchangeRateServiceTests
{
    /// <summary>驗證 TWD 換算維持原值且不需要呼叫 provider。</summary>
    [Fact]
    public async Task ConvertToBase_TwdIsIdentityWithoutProvider()
    {
        var provider = new FakeExchangeRateProvider();
        var service = new ExchangeRateService(provider, new FixedTimeProvider(DateTime.UtcNow));

        var result = service.ConvertToBase(1000m, "TWD", ExchangeRateSnapshot.Identity);

        Assert.Equal(1000m, result);
        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>驗證外幣依一 TWD 等於報價的語意進行除法換算。</summary>
    [Fact]
    public void ConvertToBase_DividesForeignAmountByTwdQuote()
    {
        var snapshot = new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m, ["USD"] = 0.031m },
            DateTime.UtcNow,
            false);

        var service = new ExchangeRateService(new FakeExchangeRateProvider(), new FixedTimeProvider(DateTime.UtcNow));

        Assert.Equal(10000m, service.ConvertToBase(310m, "USD", snapshot));
    }

    /// <summary>驗證缺少、零值與負值匯率都會回報不可換算。</summary>
    [Theory]
    [InlineData("EUR")]
    public void ConvertToBase_RejectsMissingRate(string currencyCode)
    {
        var snapshot = new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m },
            DateTime.UtcNow,
            false);
        var service = new ExchangeRateService(new FakeExchangeRateProvider(), new FixedTimeProvider(DateTime.UtcNow));

        Assert.Null(service.ConvertToBase(100m, currencyCode, snapshot));
    }

    /// <summary>驗證零值與負值匯率不會被當成可用的一比一匯率。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-0.031)]
    public void ConvertToBase_RejectsNonPositiveRate(decimal rate)
    {
        var snapshot = new ExchangeRateSnapshot(
            "TWD",
            new Dictionary<string, decimal> { ["TWD"] = 1m, ["USD"] = rate },
            DateTime.UtcNow,
            false);
        var service = new ExchangeRateService(new FakeExchangeRateProvider(), new FixedTimeProvider(DateTime.UtcNow));

        Assert.Null(service.ConvertToBase(100m, "USD", snapshot));
    }

    /// <summary>驗證一小時內的有效快取不會再次呼叫 provider。</summary>
    [Fact]
    public async Task GetSnapshotAsync_UsesFreshCache()
    {
        var clock = new FixedTimeProvider(new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc));
        var provider = new FakeExchangeRateProvider(CreateRates(0.031m));
        var service = new ExchangeRateService(provider, clock);

        var first = await service.GetSnapshotAsync();
        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        var second = await service.GetSnapshotAsync();

        Assert.Equal(1, provider.CallCount);
        Assert.False(second.IsStale);
        Assert.Equal(first.UpdatedAtUtc, second.UpdatedAtUtc);
    }

    /// <summary>驗證有效期限過後會取得新匯率並更新快取。</summary>
    [Fact]
    public async Task GetSnapshotAsync_RefreshesExpiredCache()
    {
        var clock = new FixedTimeProvider(new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc));
        var provider = new FakeExchangeRateProvider(CreateRates(0.031m), CreateRates(0.030m));
        var service = new ExchangeRateService(provider, clock);

        await service.GetSnapshotAsync();
        clock.UtcNow = clock.UtcNow.AddHours(1);
        var refreshed = await service.GetSnapshotAsync();

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(0.030m, refreshed.Rates["USD"]);
        Assert.False(refreshed.IsStale);
    }

    /// <summary>驗證更新失敗但已有快取時會回傳 stale snapshot 與原更新時間。</summary>
    [Fact]
    public async Task GetSnapshotAsync_ReturnsStaleCacheWhenRefreshFails()
    {
        var clock = new FixedTimeProvider(new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc));
        var provider = new FakeExchangeRateProvider(CreateRates(0.031m), new InvalidOperationException("provider failed"));
        var service = new ExchangeRateService(provider, clock);

        var fresh = await service.GetSnapshotAsync();
        clock.UtcNow = clock.UtcNow.AddHours(1);
        var stale = await service.GetSnapshotAsync();

        Assert.True(stale.IsStale);
        Assert.Equal(fresh.UpdatedAtUtc, stale.UpdatedAtUtc);
        Assert.Equal(0.031m, stale.Rates["USD"]);
    }

    /// <summary>驗證 provider 失敗且沒有快取時會回傳明確服務不可用錯誤。</summary>
    [Fact]
    public async Task GetSnapshotAsync_ThrowsWhenProviderFailsWithoutCache()
    {
        var provider = new FakeExchangeRateProvider(new InvalidOperationException("provider failed"));
        var service = new ExchangeRateService(provider, new FixedTimeProvider(DateTime.UtcNow));

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => service.GetSnapshotAsync());
    }

    /// <summary>驗證無 stale cache 的 provider timeout 會標示為可重試。</summary>
    [Fact]
    public async Task GetSnapshotAsync_TimeoutWithoutCacheIsRetryable()
    {
        var provider = new FakeExchangeRateProvider(new TimeoutException("timeout"));
        var service = new ExchangeRateService(provider, new FixedTimeProvider(DateTime.UtcNow));

        var error = await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            service.GetSnapshotAsync());

        Assert.True(error.IsRetryable);
    }

    /// <summary>驗證非 transient provider 失敗不會被誤標為可重試。</summary>
    [Fact]
    public async Task GetSnapshotAsync_PermanentProviderFailureIsNotRetryable()
    {
        var provider = new FakeExchangeRateProvider(new InvalidOperationException("invalid payload"));
        var service = new ExchangeRateService(provider, new FixedTimeProvider(DateTime.UtcNow));

        var error = await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            service.GetSnapshotAsync());

        Assert.False(error.IsRetryable);
    }

    /// <summary>驗證 provider 永久 HTTP 4xx 不會被標示為可重試。</summary>
    [Fact]
    public async Task GetSnapshotAsync_PermanentHttpFailureIsNotRetryable()
    {
        var provider = new FakeExchangeRateProvider(new HttpRequestException(
            "not found",
            inner: null,
            HttpStatusCode.NotFound));
        var service = new ExchangeRateService(provider, new FixedTimeProvider(DateTime.UtcNow));

        var error = await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            service.GetSnapshotAsync());

        Assert.False(error.IsRetryable);
    }

    /// <summary>驗證 provider HTTP 503 仍會被標示為可重試。</summary>
    [Fact]
    public async Task GetSnapshotAsync_TransientHttpFailureIsRetryable()
    {
        var provider = new FakeExchangeRateProvider(new HttpRequestException(
            "unavailable",
            inner: null,
            HttpStatusCode.ServiceUnavailable));
        var service = new ExchangeRateService(provider, new FixedTimeProvider(DateTime.UtcNow));

        var error = await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            service.GetSnapshotAsync());

        Assert.True(error.IsRetryable);
    }

    /// <summary>驗證非標準 600 HTTP status 不會被誤判為 5xx transient failure。</summary>
    [Fact]
    public async Task GetSnapshotAsync_NonStandardHttpFailureIsNotRetryable()
    {
        var provider = new FakeExchangeRateProvider(new HttpRequestException(
            "non-standard status",
            inner: null,
            (HttpStatusCode)600));
        var service = new ExchangeRateService(provider, new FixedTimeProvider(DateTime.UtcNow));

        var error = await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() =>
            service.GetSnapshotAsync());

        Assert.False(error.IsRetryable);
    }

    /// <summary>建立包含 TWD 與 USD 的 provider 測試匯率。</summary>
    private static IReadOnlyDictionary<string, decimal> CreateRates(decimal usdRate)
        => new Dictionary<string, decimal>
        {
            ["TWD"] = 1m,
            ["USD"] = usdRate,
            ["JPY"] = 0.22m,
            ["CNY"] = 0.22m,
            ["HKD"] = 0.25m,
        };

    /// <summary>提供依序回傳匯率或例外的測試 provider。</summary>
    private sealed class FakeExchangeRateProvider : IExchangeRateProvider
    {
        private readonly Queue<object> _responses;

        /// <summary>初始化 provider response 序列。</summary>
        public FakeExchangeRateProvider(params object[] responses)
        {
            _responses = new Queue<object>(responses);
        }

        /// <summary>取得 provider 被呼叫的次數。</summary>
        public int CallCount { get; private set; }

        /// <summary>回傳下一筆測試匯率或拋出指定例外。</summary>
        public Task<ExchangeRateProviderResult> FetchAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            var response = _responses.Count > 0 ? _responses.Dequeue() : throw new InvalidOperationException("no response");
            if (response is Exception exception)
                throw exception;
            return Task.FromResult(new ExchangeRateProviderResult((IReadOnlyDictionary<string, decimal>)response));
        }
    }

    /// <summary>提供可由測試調整的 UTC 時間來源。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        /// <summary>初始化固定 UTC 時間。</summary>
        public FixedTimeProvider(DateTime utcNow)
        {
            UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        }

        /// <summary>取得或更新目前測試 UTC 時間。</summary>
        public DateTime UtcNow { get; set; }

        /// <summary>回傳測試指定的 UTC instant。</summary>
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }
}
