using System.Globalization;
using System.Text.Json;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>封裝外部匯率 provider 回傳的正規化報價。</summary>
public sealed record ExchangeRateProviderResult(
    IReadOnlyDictionary<string, decimal> Rates,
    DateTime? UpdatedAtUtc = null);

/// <summary>定義可替換的外部匯率來源 contract。</summary>
public interface IExchangeRateProvider
{
    /// <summary>取得以 TWD 基準語意表示的匯率報價。</summary>
    Task<ExchangeRateProviderResult> FetchAsync(CancellationToken cancellationToken = default);
}

/// <summary>保存一次不可變的 TWD 基準匯率 snapshot。</summary>
public sealed record ExchangeRateSnapshot(
    string BaseCurrencyCode,
    IReadOnlyDictionary<string, decimal> Rates,
    DateTime UpdatedAtUtc,
    bool IsStale)
{
    /// <summary>建立只含 TWD identity 的匯率 snapshot。</summary>
    public static ExchangeRateSnapshot Identity => new(
        CurrencyPolicy.BaseCurrencyCode,
        new Dictionary<string, decimal>
        {
            [CurrencyPolicy.BaseCurrencyCode] = 1m,
        },
        DateTime.UnixEpoch,
        false);

    /// <summary>提供相容的匯率更新時間屬性名稱。</summary>
    public DateTime ExchangeRateUpdatedAt => UpdatedAtUtc;
}

/// <summary>表示沒有可用匯率資料的安全服務錯誤。</summary>
public sealed class ExchangeRateUnavailableException : Exception
{
    /// <summary>初始化不含 provider 原始內容的服務不可用錯誤。</summary>
    public ExchangeRateUnavailableException(
        string message = "匯率服務目前無法使用",
        Exception? innerException = null,
        bool isRetryable = false)
        : base(message, innerException)
    {
        IsRetryable = isRetryable;
    }

    /// <summary>指出失敗是否源自 transient provider 或網路原因。</summary>
    public bool IsRetryable { get; }
}

/// <summary>集中提供匯率 snapshot 與原幣轉 TWD 換算能力。</summary>
public interface IExchangeRateService
{
    /// <summary>取得一小時按需快取的匯率 snapshot。</summary>
    Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>使用指定 snapshot 將原幣金額換算為 TWD，不可換算時回傳 null。</summary>
    decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot);

    /// <summary>嘗試使用指定 snapshot 將原幣金額換算為 TWD。</summary>
    bool TryConvertToBase(
        decimal amount,
        string currencyCode,
        ExchangeRateSnapshot snapshot,
        out decimal convertedAmount);
}

/// <summary>以記憶體快取與單一更新鎖管理 TWD 基準匯率。</summary>
public sealed class ExchangeRateService : IExchangeRateService
{
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromHours(1);
    private readonly IExchangeRateProvider _provider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheDuration;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ExchangeRateSnapshot? _cachedSnapshot;

    /// <summary>初始化可注入 provider、時間來源與快取期限的匯率服務。</summary>
    public ExchangeRateService(
        IExchangeRateProvider provider,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheDuration = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheDuration = cacheDuration.GetValueOrDefault(DefaultCacheDuration);
        if (_cacheDuration <= TimeSpan.Zero)
            _cacheDuration = DefaultCacheDuration;
    }

    /// <summary>取得有效快取，或在過期後以 provider 更新並安全 fallback。</summary>
    public async Task<ExchangeRateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        if (IsFresh(_cachedSnapshot, now))
            return _cachedSnapshot!;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = UtcNow();
            if (IsFresh(_cachedSnapshot, now))
                return _cachedSnapshot!;

            try
            {
                var providerResult = await _provider.FetchAsync(cancellationToken);
                var snapshot = NormalizeProviderResult(providerResult, now);
                _cachedSnapshot = snapshot;
                return snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (_cachedSnapshot is not null)
                    return _cachedSnapshot with { IsStale = true };

                throw new ExchangeRateUnavailableException(
                    innerException: exception,
                    isRetryable: RetryClassification.IsRetryable(exception));
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>依 TWD 基準匯率除法換算原幣金額。</summary>
    public decimal? ConvertToBase(decimal amount, string currencyCode, ExchangeRateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!CurrencyPolicy.TryNormalize(currencyCode, out var normalizedCurrencyCode))
            return null;
        if (!snapshot.BaseCurrencyCode.Equals(CurrencyPolicy.BaseCurrencyCode, StringComparison.Ordinal))
            return null;
        if (normalizedCurrencyCode == CurrencyPolicy.BaseCurrencyCode)
            return amount;
        if (!snapshot.Rates.TryGetValue(normalizedCurrencyCode, out var rate) || rate <= 0m)
            return null;

        try
        {
            return amount / rate;
        }
        catch (DivideByZeroException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>回傳可用的 TWD 換算結果，並以 false 表示匯率缺失。</summary>
    public bool TryConvertToBase(
        decimal amount,
        string currencyCode,
        ExchangeRateSnapshot snapshot,
        out decimal convertedAmount)
    {
        var converted = ConvertToBase(amount, currencyCode, snapshot);
        if (converted.HasValue)
        {
            convertedAmount = converted.Value;
            return true;
        }

        convertedAmount = 0m;
        return false;
    }

    /// <summary>判斷快取時間是否仍在有效期限內。</summary>
    private bool IsFresh(ExchangeRateSnapshot? snapshot, DateTime now)
    {
        if (snapshot is null)
            return false;

        var age = now - snapshot.UpdatedAtUtc;
        return age >= TimeSpan.Zero && age < _cacheDuration;
    }

    /// <summary>將 provider 回應整理成固定 TWD 基準的 immutable snapshot。</summary>
    private static ExchangeRateSnapshot NormalizeProviderResult(
        ExchangeRateProviderResult providerResult,
        DateTime fallbackTimestamp)
    {
        ArgumentNullException.ThrowIfNull(providerResult);
        var rates = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            [CurrencyPolicy.BaseCurrencyCode] = 1m,
        };

        foreach (var pair in providerResult.Rates)
        {
            if (!CurrencyPolicy.TryNormalize(pair.Key, out var currencyCode) ||
                currencyCode == CurrencyPolicy.BaseCurrencyCode)
            {
                continue;
            }

            rates[currencyCode] = pair.Value;
        }

        var updatedAt = providerResult.UpdatedAtUtc ?? fallbackTimestamp;
        updatedAt = DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc);
        return new ExchangeRateSnapshot(
            CurrencyPolicy.BaseCurrencyCode,
            rates,
            updatedAt,
            false);
    }

    /// <summary>取得明確標示為 UTC 的目前時間。</summary>
    private DateTime UtcNow()
        => DateTime.SpecifyKind(_timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
}

/// <summary>依帳戶集合決定是否需要取得外部匯率 snapshot。</summary>
public static class ExchangeRateSnapshotResolver
{
    /// <summary>只有存在外幣帳戶時才取得一次匯率，否則回傳 TWD identity。</summary>
    public static async Task<ExchangeRateSnapshot> ResolveForAccountsAsync(
        IEnumerable<BankAccount> bankAccounts,
        IExchangeRateService? exchangeRateService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bankAccounts);
        var requiresExchangeRate = bankAccounts.Any(account =>
            CurrencyPolicy.NormalizeOrDefault(account.CurrencyCode) != CurrencyPolicy.BaseCurrencyCode);
        if (!requiresExchangeRate)
            return ExchangeRateSnapshot.Identity;
        if (exchangeRateService is null)
            throw new ExchangeRateUnavailableException("存在外幣帳戶但未設定匯率服務");

        return await exchangeRateService.GetSnapshotAsync(cancellationToken);
    }
}

/// <summary>呼叫 open.er-api.com 並轉成 TWD 基準報價的 provider。</summary>
public sealed class OpenExchangeRateProvider : IExchangeRateProvider
{
    private const string Endpoint = "https://open.er-api.com/v6/latest/USD";
    private readonly HttpClient _httpClient;

    /// <summary>初始化指定的 HTTP client。</summary>
    public OpenExchangeRateProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>取得外部 USD 報價並轉換成一 TWD 等於報價的語意。</summary>
    public async Task<ExchangeRateProviderResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(Endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("rates", out var ratesElement) ||
            ratesElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("匯率服務回應缺少 rates");
        }

        var sourceRates = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var property in ratesElement.EnumerateObject())
        {
            if (decimal.TryParse(
                    property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.GetRawText(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var rate))
            {
                sourceRates[property.Name.ToUpperInvariant()] = rate;
            }
        }

        if (!sourceRates.TryGetValue(CurrencyPolicy.BaseCurrencyCode, out var usdToTwd) || usdToTwd <= 0m)
            throw new InvalidOperationException("匯率服務回應缺少有效 TWD 匯率");

        var normalizedRates = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            [CurrencyPolicy.BaseCurrencyCode] = 1m,
        };
        foreach (var currencyCode in CurrencyPolicy.SupportedCurrencies)
        {
            if (currencyCode == CurrencyPolicy.BaseCurrencyCode ||
                !sourceRates.TryGetValue(currencyCode, out var sourceRate) ||
                sourceRate <= 0m)
            {
                continue;
            }

            normalizedRates[currencyCode] = Math.Round(
                sourceRate / usdToTwd,
                12,
                MidpointRounding.AwayFromZero);
        }

        return new ExchangeRateProviderResult(normalizedRates);
    }
}
