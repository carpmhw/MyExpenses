using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public static class InstallmentPaymentMarker
{
    /// <summary>Toggles an installment payment status and stores the provided paid date when marking paid.</summary>
    public static void TogglePaid(InstallmentPayment payment, DateOnly? paidDate)
    {
        if (!payment.IsPaid && !paidDate.HasValue)
            throw new ArgumentException("請選擇實際繳款日");

        payment.IsPaid = !payment.IsPaid;
        payment.PaidDate = payment.IsPaid ? paidDate!.Value.ToDateTime(TimeOnly.MinValue) : null;
    }
}
