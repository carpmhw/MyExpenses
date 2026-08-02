using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public static class InstallmentPaymentMarker
{
    /// <summary>Sets an installment payment to the requested target state without toggle semantics.</summary>
    public static void SetPaidState(InstallmentPayment payment, bool isPaid, DateOnly? paidDate)
    {
        if (isPaid && !paidDate.HasValue)
            throw new ArgumentException("請選擇實際繳款日");

        payment.IsPaid = isPaid;
        payment.PaidDate = isPaid ? paidDate : null;
    }
}
