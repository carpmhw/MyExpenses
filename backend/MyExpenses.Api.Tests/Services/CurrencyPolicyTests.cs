using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class CurrencyPolicyTests
{
    /// <summary>驗證首版固定支援五種貨幣代碼。</summary>
    [Theory]
    [InlineData("TWD")]
    [InlineData("USD")]
    [InlineData("JPY")]
    [InlineData("CNY")]
    [InlineData("HKD")]
    public void IsSupported_AcceptsConfiguredCurrencies(string currencyCode)
    {
        Assert.True(CurrencyPolicy.IsSupported(currencyCode));
    }

    /// <summary>驗證貨幣代碼會移除外部空白並轉成大寫。</summary>
    [Theory]
    [InlineData(" usd ", "USD")]
    [InlineData(" jPy ", "JPY")]
    public void Normalize_TrimsAndUppercasesCurrencyCode(string input, string expected)
    {
        Assert.Equal(expected, CurrencyPolicy.Normalize(input));
    }

    /// <summary>驗證省略或空白貨幣代碼會使用 TWD 預設值。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeOrDefault_UsesTwdForMissingCurrency(string? input)
    {
        Assert.Equal(CurrencyPolicy.BaseCurrencyCode, CurrencyPolicy.NormalizeOrDefault(input));
    }

    /// <summary>驗證不支援的貨幣代碼會被拒絕而不是被猜測成其他貨幣。</summary>
    [Theory]
    [InlineData("EUR")]
    [InlineData("US")]
    [InlineData("USDT")]
    [InlineData("ABC")]
    public void Normalize_RejectsUnsupportedCurrencyCode(string input)
    {
        Assert.Throws<ArgumentException>(() => CurrencyPolicy.Normalize(input));
    }
}
