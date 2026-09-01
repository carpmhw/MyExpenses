using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class InstallmentScheduleCalculatorTests
{
    /// <summary>Verifies purchases before statement day stay in the purchase month.</summary>
    [Fact]
    public void CalculateDueDate_UsesPurchaseMonthWhenPurchaseIsOnOrBeforeStatementDay()
    {
        var dueDate = InstallmentScheduleCalculator.CalculateDueDate(new DateOnly(2026, 6, 10), 15, 23, 1);

        Assert.Equal(new DateOnly(2026, 6, 23), dueDate);
    }

    /// <summary>Verifies purchases after statement day start from the next billing cycle.</summary>
    [Fact]
    public void CalculateDueDate_UsesNextMonthWhenPurchaseIsAfterStatementDay()
    {
        var dueDate = InstallmentScheduleCalculator.CalculateDueDate(new DateOnly(2026, 6, 16), 15, 23, 1);

        Assert.Equal(new DateOnly(2026, 7, 23), dueDate);
    }

    /// <summary>Verifies due dates clamp to the last day of shorter months.</summary>
    [Fact]
    public void CalculateDueDate_ClampsDueDayToMonthEnd()
    {
        var dueDate = InstallmentScheduleCalculator.CalculateDueDate(new DateOnly(2026, 1, 10), 15, 31, 2);

        Assert.Equal(new DateOnly(2026, 2, 28), dueDate);
    }

    /// <summary>Verifies installment amounts use the final period to absorb the remainder.</summary>
    [Fact]
    public void CalculateAmounts_AssignsRemainderToFinalPeriod()
    {
        var amounts = InstallmentScheduleCalculator.CalculateAmounts(100m, 3);

        Assert.Equal(new[] { 33m, 33m, 34m }, amounts);
    }

    /// <summary>驗證一期交易的付款金額就是完整總額。</summary>
    [Fact]
    public void CalculateAmounts_AcceptsOnePeriodAsFullAmount()
    {
        var amounts = InstallmentScheduleCalculator.CalculateAmounts(100m, 1);

        Assert.Equal(new[] { 100m }, amounts);
    }

    /// <summary>Verifies schedule amount calculation rejects non-positive totals and periods.</summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 3)]
    [InlineData(100, 0)]
    public void CalculateAmounts_RejectsInvalidInput(decimal totalAmount, int periods)
    {
        Assert.Throws<ArgumentException>(() => InstallmentScheduleCalculator.CalculateAmounts(totalAmount, periods));
    }
}
