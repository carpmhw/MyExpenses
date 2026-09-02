using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class InstallmentCommandValidatorTests
{
    /// <summary>Verifies expense categories are accepted by installment commands.</summary>
    [Fact]
    public void ValidateExpenseCategory_AcceptsExpenseCategory()
    {
        var category = new Category { Type = CategoryType.Expense };

        InstallmentCommandValidator.ValidateExpenseCategory(category);
    }

    /// <summary>Verifies income categories are rejected by installment commands.</summary>
    [Fact]
    public void ValidateExpenseCategory_RejectsIncomeCategory()
    {
        var category = new Category { Type = CategoryType.Income };

        var error = Assert.Throws<FinancialCommandException>(
            () => InstallmentCommandValidator.ValidateExpenseCategory(category));

        Assert.Equal(422, error.StatusCode);
    }

    /// <summary>驗證一期信用卡交易可通過共用排程驗證。</summary>
    [Fact]
    public void ValidateSchedule_AcceptsOnePeriod()
    {
        InstallmentCommandValidator.ValidateSchedule(100m, 1, new DateOnly(2026, 6, 20));
    }

    /// <summary>驗證六十期仍可通過共用排程驗證。</summary>
    [Fact]
    public void ValidateSchedule_AcceptsSixtyPeriods()
    {
        InstallmentCommandValidator.ValidateSchedule(6000m, 60, new DateOnly(2026, 6, 20));
    }

    /// <summary>驗證零期仍會被共用排程驗證拒絕。</summary>
    [Fact]
    public void ValidateSchedule_RejectsZeroPeriods()
    {
        var error = Assert.Throws<FinancialCommandException>(
            () => InstallmentCommandValidator.ValidateSchedule(100m, 0, new DateOnly(2026, 6, 20)));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("期數必須為 1 至 60 期", error.Detail);
    }

    /// <summary>驗證超過六十期會被共用排程驗證拒絕並回傳一致訊息。</summary>
    [Fact]
    public void ValidateSchedule_RejectsMoreThanSixtyPeriods()
    {
        var error = Assert.Throws<FinancialCommandException>(
            () => InstallmentCommandValidator.ValidateSchedule(6100m, 61, new DateOnly(2026, 6, 20)));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("期數必須為 1 至 60 期", error.Detail);
    }

    /// <summary>Verifies only the credit-card payment method is accepted for installment purchases.</summary>
    [Fact]
    public void ValidateCreditCardPaymentMethod_RejectsOtherSystemCode()
    {
        var paymentMethod = new PaymentMethod { SystemCode = "cash" };

        var error = Assert.Throws<FinancialCommandException>(
            () => InstallmentCommandValidator.ValidateCreditCardPaymentMethod(paymentMethod));

        Assert.Equal(422, error.StatusCode);
    }
}
