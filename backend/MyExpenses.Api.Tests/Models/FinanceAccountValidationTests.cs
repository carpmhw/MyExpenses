using System.ComponentModel.DataAnnotations;
using MyExpenses.Api.Models;
using Xunit;

namespace MyExpenses.Api.Tests.Models;

public class FinanceAccountValidationTests
{
    /// <summary>Verifies credit card suffixes must be exactly four digits.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("12A4")]
    [InlineData("12345")]
    public void CreditCard_RejectsInvalidLastFourDigits(string lastFourDigits)
    {
        var card = new CreditCard { BankName = "測試銀行", LastFourDigits = lastFourDigits };

        var isValid = Validator.TryValidateObject(card, new ValidationContext(card), [], true);

        Assert.False(isValid);
    }

    /// <summary>Verifies credit card network values are limited to supported card networks.</summary>
    [Theory]
    [InlineData("Discover")]
    [InlineData("visa")]
    [InlineData("威士")]
    public void CreditCard_RejectsInvalidCardNetwork(string cardNetwork)
    {
        var card = new CreditCard { BankName = "測試銀行", LastFourDigits = "1234" };
        var property = typeof(CreditCard).GetProperty("CardNetwork");
        Assert.NotNull(property);
        property.SetValue(card, cardNetwork);

        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(card, new ValidationContext(card), validationResults, true);

        Assert.False(isValid);
        Assert.Contains(validationResults, r => r.ErrorMessage == "卡種必須為有效的國際卡別網路");
    }

    /// <summary>Verifies credit card notes are limited to 200 characters.</summary>
    [Fact]
    public void CreditCard_RejectsNotesLongerThan200Characters()
    {
        var card = new CreditCard { BankName = "測試銀行", LastFourDigits = "1234" };
        var property = typeof(CreditCard).GetProperty("Notes");
        Assert.NotNull(property);
        property.SetValue(card, new string('A', 201));

        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(card, new ValidationContext(card), validationResults, true);

        Assert.False(isValid);
        Assert.Contains(validationResults, r => r.ErrorMessage == "備註最多 200 字元");
    }

    /// <summary>Verifies bank account suffixes must be exactly five digits.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("12A45")]
    [InlineData("123456")]
    public void BankAccount_RejectsInvalidAccountNumberSuffix(string accountNumber)
    {
        var account = new BankAccount { BankName = "測試銀行", AccountNumber = accountNumber };

        var isValid = Validator.TryValidateObject(account, new ValidationContext(account), [], true);

        Assert.False(isValid);
    }
}
