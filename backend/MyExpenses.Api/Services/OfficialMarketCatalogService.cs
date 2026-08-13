namespace MyExpenses.Api.Services;

/// <summary>定義官方雙市場 catalog 的直接取得與單一代號 lookup contract。</summary>
public interface IOfficialMarketCatalogService
{
    /// <summary>不使用互動式快取取得同一次的完整雙市場結果。</summary>
    Task<OfficialMarketCatalogSnapshot> FetchAsync(CancellationToken cancellationToken = default);

    /// <summary>使用完整快照快取解析單一代號的官方市場資訊。</summary>
    Task<OfficialMarketResolution> LookupAsync(
        string? symbol,
        CancellationToken cancellationToken = default);
}

/// <summary>保存完整雙市場成功快照的一小時原子快取。</summary>
public sealed class OfficialMarketCatalogCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private OfficialMarketCatalogSnapshot? _snapshot;
    private Task<OfficialMarketCatalogSnapshot>? _inFlightRefresh;
    private DateTimeOffset _expiresAtUtc;

    /// <summary>初始化具 bounded TTL 與可測試時間來源的 catalog 快取。</summary>
    public OfficialMarketCatalogCache(
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = ttl.GetValueOrDefault(DefaultTtl);
        if (_ttl <= TimeSpan.Zero)
            _ttl = DefaultTtl;
    }

    /// <summary>取得仍有效的快照，或共享同一個進行中的雙市場刷新工作。</summary>
    public async Task<OfficialMarketCatalogSnapshot> GetOrRefreshAsync(
        Func<CancellationToken, Task<OfficialMarketCatalogSnapshot>> refresh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        var cached = GetFreshSnapshot();
        if (cached is not null)
            return cached;

        TaskCompletionSource<OfficialMarketCatalogSnapshot>? refreshCompletion = null;
        Task<OfficialMarketCatalogSnapshot> refreshTask;
        lock (_sync)
        {
            if (_snapshot is not null && _expiresAtUtc > _timeProvider.GetUtcNow())
                return _snapshot;
            if (_inFlightRefresh is null)
            {
                refreshCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlightRefresh = refreshCompletion.Task;
            }

            refreshTask = _inFlightRefresh;
        }

        if (refreshCompletion is not null)
            _ = RunRefreshAsync(refresh, refreshCompletion);

        return await refreshTask.WaitAsync(cancellationToken);
    }

    /// <summary>執行不受個別 waiter 取消影響的刷新，並發布完整快照與清除同一工作。</summary>
    private async Task RunRefreshAsync(
        Func<CancellationToken, Task<OfficialMarketCatalogSnapshot>> refresh,
        TaskCompletionSource<OfficialMarketCatalogSnapshot> completion)
    {
        OfficialMarketCatalogSnapshot? refreshed = null;
        Exception? failure = null;
        try
        {
            refreshed = await refresh(CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_inFlightRefresh, completion.Task))
            {
                if (failure is null && IsComplete(refreshed!))
                {
                    _snapshot = refreshed;
                    _expiresAtUtc = _timeProvider.GetUtcNow().Add(_ttl);
                }

                _inFlightRefresh = null;
            }
        }

        if (failure is null)
            completion.TrySetResult(refreshed!);
        else
            completion.TrySetException(failure);
    }

    /// <summary>取得尚未過期的完整快照，避免發布半更新的雙市場資料。</summary>
    private OfficialMarketCatalogSnapshot? GetFreshSnapshot()
    {
        lock (_sync)
        {
            return _snapshot is not null && _expiresAtUtc > _timeProvider.GetUtcNow()
                ? _snapshot
                : null;
        }
    }

    /// <summary>判斷兩個官方 provider 是否都成功回傳可用 catalog。</summary>
    private static bool IsComplete(OfficialMarketCatalogSnapshot snapshot)
        => snapshot.Twse.Failure is null
            && snapshot.Tpex.Failure is null
            && snapshot.Twse.Records.Count > 0
            && snapshot.Tpex.Records.Count > 0;
}

/// <summary>以兩個既有官方 current-price provider 組合市場 catalog。</summary>
public sealed class OfficialMarketCatalogService : IOfficialMarketCatalogService
{
    private readonly ICurrentPriceProvider _twseProvider;
    private readonly ICurrentPriceProvider _tpexProvider;
    private readonly OfficialMarketCatalogCache _cache;

    /// <summary>初始化官方 catalog service 與互動式快取。</summary>
    public OfficialMarketCatalogService(
        ICurrentPriceProvider twseProvider,
        ICurrentPriceProvider tpexProvider,
        OfficialMarketCatalogCache? cache = null)
    {
        _twseProvider = twseProvider ?? throw new ArgumentNullException(nameof(twseProvider));
        _tpexProvider = tpexProvider ?? throw new ArgumentNullException(nameof(tpexProvider));
        _cache = cache ?? new OfficialMarketCatalogCache();
    }

    /// <summary>並行取得兩個官方市場清單，並將非預期例外轉為安全 provider failure。</summary>
    public async Task<OfficialMarketCatalogSnapshot> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        var twseTask = FetchProviderAsync(_twseProvider, cancellationToken);
        var tpexTask = FetchProviderAsync(_tpexProvider, cancellationToken);
        await Task.WhenAll(twseTask, tpexTask);
        return new(await twseTask, await tpexTask);
    }

    /// <summary>以原子完整快照解析單一代號，來源失敗時不使用舊快照猜測。</summary>
    public async Task<OfficialMarketResolution> LookupAsync(
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _cache.GetOrRefreshAsync(FetchAsync, cancellationToken);
        return OfficialMarketCatalogResolver.Resolve(snapshot, symbol);
    }

    /// <summary>執行單一官方 provider 並保留取消與 bounded failure 語意。</summary>
    private static async Task<CurrentPriceProviderResult> FetchProviderAsync(
        ICurrentPriceProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.FetchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return CurrentPriceProviderResult.Failed(
                provider.ProviderName,
                "NetworkError",
                "行情服務連線失敗",
                true);
        }
        catch (Exception exception)
        {
            var retryable = RetryClassification.IsRetryable(exception);
            return CurrentPriceProviderResult.Failed(
                provider.ProviderName,
                retryable ? "NetworkError" : "ProviderFailure",
                retryable ? "行情服務連線失敗" : "行情服務無法使用",
                retryable);
        }
    }
}
