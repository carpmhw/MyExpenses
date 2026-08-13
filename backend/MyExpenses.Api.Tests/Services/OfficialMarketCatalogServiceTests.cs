using System.Reflection;
using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class OfficialMarketCatalogServiceTests
{
    /// <summary>驗證 lookup 會以完整雙來源快照解析代號並在 TTL 內重用結果。</summary>
    [Fact]
    public async Task LookupAsync_UsesCompleteSnapshotAndCachesWithinTtl()
    {
        var twse = new FakeProvider(
            StockMarket.Twse,
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m, "台積電")]));
        var tpex = new FakeProvider(
            StockMarket.Tpex,
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m, "環球晶")]));
        var service = new OfficialMarketCatalogService(
            twse,
            tpex,
            new OfficialMarketCatalogCache(ttl: TimeSpan.FromHours(1)));

        var first = await service.LookupAsync("2330");
        var second = await service.LookupAsync("6488");

        Assert.Equal(StockMarket.Twse, first.Market);
        Assert.Equal(StockMarket.Tpex, second.Market);
        Assert.Equal(1, twse.CallCount);
        Assert.Equal(1, tpex.CallCount);
    }

    /// <summary>驗證完整快取不會在任一官方來源失敗時發布。</summary>
    [Fact]
    public async Task LookupAsync_DoesNotReuseExpiredSnapshotWhenRefreshFails()
    {
        var twse = new FakeProvider(
            StockMarket.Twse,
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]));
        var tpex = new FakeProvider(
            StockMarket.Tpex,
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)]));
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-12T00:00:00Z"));
        var service = new OfficialMarketCatalogService(
            twse,
            tpex,
            new OfficialMarketCatalogCache(clock, TimeSpan.FromMinutes(1)));

        Assert.Equal(StockMarket.Twse, (await service.LookupAsync("2330")).Market);
        clock.Advance(TimeSpan.FromMinutes(2));
        tpex.Result = CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true);

        var result = await service.LookupAsync("2330");

        Assert.Equal(StockMarket.Unknown, result.Market);
        Assert.Equal("MarketDetectionUnavailable", result.Code);
        Assert.Equal(2, twse.CallCount);
        Assert.Equal(2, tpex.CallCount);
    }

    /// <summary>驗證並行 lookup 只會由一個 refresh 發布完整官方快照。</summary>
    [Fact]
    public async Task LookupAsync_SerializesConcurrentRefreshes()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twse = new BlockingProvider(
            "TWSE",
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            release);
        var tpex = new BlockingProvider(
            "TPEx",
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)]),
            release);
        var service = new OfficialMarketCatalogService(
            twse,
            tpex,
            new OfficialMarketCatalogCache(ttl: TimeSpan.FromHours(1)));

        var first = service.LookupAsync("2330");
        await Task.WhenAll(twse.Started.Task, tpex.Started.Task);
        var second = service.LookupAsync("6488");
        release.SetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(StockMarket.Twse, results[0].Market);
        Assert.Equal(StockMarket.Tpex, results[1].Market);
        Assert.Equal(1, twse.CallCount);
        Assert.Equal(1, tpex.CallCount);
    }

    /// <summary>驗證並行 lookup 共享同一個失敗 refresh，避免排隊 caller 重複請求來源。</summary>
    [Fact]
    public async Task LookupAsync_SharesConcurrentFailedRefresh()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twse = new BlockingProvider(
            "TWSE",
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            release);
        var tpex = new BlockingProvider(
            "TPEx",
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true),
            release);
        var service = new OfficialMarketCatalogService(
            twse,
            tpex,
            new OfficialMarketCatalogCache(ttl: TimeSpan.FromHours(1)));

        var first = service.LookupAsync("2330");
        await Task.WhenAll(twse.Started.Task, tpex.Started.Task);
        var second = service.LookupAsync("6488");
        release.SetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
        {
            Assert.Equal(StockMarket.Unknown, result.Market);
            Assert.Equal("MarketDetectionUnavailable", result.Code);
        });
        Assert.Equal(1, twse.CallCount);
        Assert.Equal(1, tpex.CallCount);
    }

    /// <summary>驗證共享失敗 refresh 完成後的 sequential lookup 會重新取得來源。</summary>
    [Fact]
    public async Task LookupAsync_RetriesAfterSharedFailedRefreshCompletes()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twse = new BlockingProvider(
            "TWSE",
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            release);
        var tpex = new BlockingProvider(
            "TPEx",
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true),
            release);
        var service = new OfficialMarketCatalogService(
            twse,
            tpex,
            new OfficialMarketCatalogCache(ttl: TimeSpan.FromHours(1)));

        var first = service.LookupAsync("2330");
        await Task.WhenAll(twse.Started.Task, tpex.Started.Task);
        var second = service.LookupAsync("6488");
        release.SetResult(true);
        await Task.WhenAll(first, second);

        var third = await service.LookupAsync("2330");

        Assert.Equal(StockMarket.Unknown, third.Market);
        Assert.Equal("MarketDetectionUnavailable", third.Code);
        Assert.Equal(2, twse.CallCount);
        Assert.Equal(2, tpex.CallCount);
    }

    /// <summary>驗證單一 waiter 取消不會取消其他 caller 共用的 refresh。</summary>
    [Fact]
    public async Task LookupAsync_CancelsOnlyTheWaitingCaller()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twse = new BlockingProvider(
            "TWSE",
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            release);
        var tpex = new BlockingProvider(
            "TPEx",
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)]),
            release);
        var service = new OfficialMarketCatalogService(
            twse,
            tpex,
            new OfficialMarketCatalogCache(ttl: TimeSpan.FromHours(1)));
        using var cancellation = new CancellationTokenSource();

        var first = service.LookupAsync("2330");
        await Task.WhenAll(twse.Started.Task, tpex.Started.Task);
        var second = service.LookupAsync("6488", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult(true);
        var result = await first;

        Assert.Equal(StockMarket.Twse, result.Market);
        Assert.Equal(1, twse.CallCount);
        Assert.Equal(1, tpex.CallCount);
    }

    /// <summary>驗證未取消 caller token 的 provider OCE 會映射為可重試 timeout failure。</summary>
    [Fact]
    public async Task FetchAsync_ClassifiesNonHostCancellationAsRetryableTimeout()
    {
        var service = new OfficialMarketCatalogService(
            new ThrowingProvider("TWSE", StockMarket.Twse, new OperationCanceledException("內部 timeout")),
            new FakeProvider(
                StockMarket.Tpex,
                CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)])));

        var snapshot = await service.FetchAsync();

        Assert.NotNull(snapshot.Twse.Failure);
        Assert.Equal("NetworkError", snapshot.Twse.Failure.Code);
        Assert.True(snapshot.Twse.Failure.Retryable);
        Assert.Equal("行情服務連線失敗", snapshot.Twse.Failure.SafeMessage);
    }

    /// <summary>驗證 refresh 結果只會在清除同一個 in-flight task 後才對 waiter 完成。</summary>
    [Fact]
    public async Task GetOrRefreshAsync_ClearsFailedInFlightRefreshBeforeCompletingWaiters()
    {
        var cache = new OfficialMarketCatalogCache(ttl: TimeSpan.FromHours(1));
        var failedSnapshot = new OfficialMarketCatalogSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true));
        var refreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<OfficialMarketCatalogSnapshot>();
        var first = Task.Run(() => cache.GetOrRefreshAsync(async _ =>
        {
            refreshStarted.TrySetResult(true);
            return await releaseRefresh.Task.ConfigureAwait(false);
        }));
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var syncField = typeof(OfficialMarketCatalogCache).GetField(
            "_sync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var inFlightField = typeof(OfficialMarketCatalogCache).GetField(
            "_inFlightRefresh",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(syncField);
        Assert.NotNull(inFlightField);
        var sync = Assert.IsType<object>(syncField.GetValue(cache));
        var inFlight = Assert.IsAssignableFrom<Task>(inFlightField.GetValue(cache));
        using var lockHeld = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        var lockHolder = new Thread(() =>
        {
            lock (sync)
            {
                lockHeld.Set();
                releaseLock.Wait();
            }
        });
        var refreshReleaser = new Thread(() => releaseRefresh.SetResult(failedSnapshot));

        lockHolder.Start();
        Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)), "專用 thread 未能持有 cache lock");
        try
        {
            refreshReleaser.Start();
            Assert.True(
                SpinWait.SpinUntil(
                    () => (refreshReleaser.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)),
                "refresh thread 未在 cache lock 處阻塞");
            Assert.False(inFlight.IsCompleted);
        }
        finally
        {
            releaseLock.Set();
            Assert.True(lockHolder.Join(TimeSpan.FromSeconds(5)), "cache lock holder 未正常結束");
            Assert.True(refreshReleaser.Join(TimeSpan.FromSeconds(5)), "refresh releaser 未正常結束");
        }

        Assert.Same(failedSnapshot, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(inFlightField.GetValue(cache));
    }

    /// <summary>保存測試用官方 provider 的結果與呼叫次數。</summary>
    private sealed class FakeProvider : ICurrentPriceProvider
    {
        /// <summary>初始化指定市場與預設結果。</summary>
        public FakeProvider(StockMarket market, CurrentPriceProviderResult result)
        {
            Market = market;
            ProviderName = result.Provider;
            Result = result;
        }

        /// <summary>取得 provider 的安全名稱。</summary>
        public string ProviderName { get; }

        /// <summary>取得 provider 對應市場。</summary>
        public StockMarket Market { get; }

        /// <summary>取得或更新測試要回傳的結果。</summary>
        public CurrentPriceProviderResult Result { get; set; }

        /// <summary>取得 provider 被呼叫的次數。</summary>
        public int CallCount { get; private set; }

        /// <summary>回傳測試指定的官方市場清單。</summary>
        public Task<CurrentPriceProviderResult> FetchAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    /// <summary>提供直接拋出指定例外的官方 provider。</summary>
    private sealed class ThrowingProvider : ICurrentPriceProvider
    {
        private readonly Exception _exception;

        /// <summary>初始化 provider 名稱、市場與要拋出的例外。</summary>
        public ThrowingProvider(string providerName, StockMarket market, Exception exception)
        {
            ProviderName = providerName;
            Market = market;
            _exception = exception;
        }

        /// <summary>取得 provider 安全名稱。</summary>
        public string ProviderName { get; }

        /// <summary>取得 provider 對應市場。</summary>
        public StockMarket Market { get; }

        /// <summary>拋出測試指定的 raw provider 例外。</summary>
        public Task<CurrentPriceProviderResult> FetchAsync(CancellationToken cancellationToken = default)
            => Task.FromException<CurrentPriceProviderResult>(_exception);
    }

    /// <summary>提供可控制完成時機的官方 provider 以驗證快取刷新鎖。</summary>
    private sealed class BlockingProvider : ICurrentPriceProvider
    {
        private readonly CurrentPriceProviderResult _result;
        private readonly TaskCompletionSource<bool> _release;

        /// <summary>初始化可阻塞的 provider。</summary>
        public BlockingProvider(
            string providerName,
            CurrentPriceProviderResult result,
            TaskCompletionSource<bool> release)
        {
            ProviderName = providerName;
            Market = providerName == "TWSE" ? StockMarket.Twse : StockMarket.Tpex;
            _result = result;
            _release = release;
        }

        /// <summary>取得 provider 的安全名稱。</summary>
        public string ProviderName { get; }

        /// <summary>取得 provider 對應市場。</summary>
        public StockMarket Market { get; }

        /// <summary>取得 provider 被呼叫的次數。</summary>
        public int CallCount { get; private set; }

        /// <summary>表示 provider 已開始第一次請求。</summary>
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>等待測試釋放後回傳固定官方資料。</summary>
        public async Task<CurrentPriceProviderResult> FetchAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return _result;
        }
    }

    /// <summary>提供可前進的 UTC 測試時間來源。</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        /// <summary>初始化目前時間。</summary>
        public ManualTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        /// <summary>取得測試時間。</summary>
        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>前進測試時間。</summary>
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
