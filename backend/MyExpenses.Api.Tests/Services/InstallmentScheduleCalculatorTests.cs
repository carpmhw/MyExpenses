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
}
