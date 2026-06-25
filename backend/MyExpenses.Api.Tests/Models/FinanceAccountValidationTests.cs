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
