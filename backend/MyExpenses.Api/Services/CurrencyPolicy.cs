namespace MyExpenses.Api.Services;

/// <summary>集中管理系統支援的貨幣代碼與正規化規則。</summary>
public static class CurrencyPolicy
{
    /// <summary>取得系統固定使用的基準貨幣。</summary>
    public const string BaseCurrencyCode = "TWD";

    /// <summary>取得省略貨幣代碼時使用的預設貨幣。</summary>
    public const string DefaultCurrencyCode = BaseCurrencyCode;

    /// <summary>取得首版允許的貨幣代碼集合。</summary>
    public static IReadOnlySet<string> SupportedCurrencyCodes { get; } =
        new HashSet<string>(["TWD", "USD", "JPY", "CNY", "HKD"], StringComparer.Ordinal);

    /// <summary>取得固定排序的支援貨幣代碼清單。</summary>
    public static IReadOnlyList<string> SupportedCurrencies { get; } =
        ["TWD", "USD", "JPY", "CNY", "HKD"];

    /// <summary>判斷輸入是否為正規化後的支援貨幣代碼。</summary>
    public static bool IsSupported(string? currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
            return false;

        return SupportedCurrencyCodes.Contains(currencyCode.Trim().ToUpperInvariant());
    }

    /// <summary>移除外部空白、轉成大寫並驗證貨幣代碼。</summary>
    public static string Normalize(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
            throw new ArgumentException("貨幣代碼不可為空白", nameof(currencyCode));

        var normalized = currencyCode.Trim().ToUpperInvariant();
        if (!SupportedCurrencyCodes.Contains(normalized))
            throw new ArgumentException("不支援的貨幣代碼", nameof(currencyCode));

        return normalized;
    }

    /// <summary>將省略或空白輸入套用預設值，再正規化有效貨幣代碼。</summary>
    public static string NormalizeOrDefault(string? currencyCode)
        => string.IsNullOrWhiteSpace(currencyCode)
            ? DefaultCurrencyCode
            : Normalize(currencyCode);

    /// <summary>嘗試正規化貨幣代碼，失敗時不拋出例外。</summary>
    public static bool TryNormalize(string? currencyCode, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            normalized = DefaultCurrencyCode;
            return true;
        }

        normalized = currencyCode.Trim().ToUpperInvariant();
        return SupportedCurrencyCodes.Contains(normalized);
    }
}
