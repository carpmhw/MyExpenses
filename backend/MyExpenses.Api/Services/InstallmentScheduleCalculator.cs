namespace MyExpenses.Api.Services;

public static class InstallmentScheduleCalculator
{
    /// <summary>Splits a positive total into periods and assigns any remainder to the final period.</summary>
    public static IReadOnlyList<decimal> CalculateAmounts(decimal totalAmount, int periods)
    {
        if (totalAmount <= 0)
            throw new ArgumentException("總金額必須大於零", nameof(totalAmount));
        if (periods < 1)
            throw new ArgumentException("期數必須至少為 1 期", nameof(periods));

        var perPeriod = Math.Floor(totalAmount / periods);
        var remainder = totalAmount - perPeriod * periods;
        var amounts = Enumerable.Repeat(perPeriod, periods).ToArray();
        amounts[^1] += remainder;
        return amounts;
    }

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
