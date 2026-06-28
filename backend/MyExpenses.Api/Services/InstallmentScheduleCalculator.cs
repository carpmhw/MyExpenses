namespace MyExpenses.Api.Services;

public static class InstallmentScheduleCalculator
{
    /// <summary>Calculates an installment payment due date from the purchase date and credit card billing cycle.</summary>
    public static DateOnly CalculateDueDate(DateOnly purchaseDate, int statementDay, int dueDay, int periodIndex)
    {
        var offset = purchaseDate.Day > statementDay ? 1 : 0;
        var targetMonth = purchaseDate.Month + offset + periodIndex - 1;

        var year = purchaseDate.Year + (targetMonth - 1) / 12;
        var month = ((targetMonth - 1) % 12) + 1;
        var day = Math.Min(dueDay, DateTime.DaysInMonth(year, month));

        return new DateOnly(year, month, day);
    }
}
