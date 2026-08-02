using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class InstallmentPaymentMarkerTests
{
    /// <summary>Verifies setting an unpaid period paid stores the user-provided paid date.</summary>
    [Fact]
    public void SetPaidState_StoresProvidedPaidDateWhenMarkingPaid()
    {
        var payment = new InstallmentPayment { IsPaid = false };

        InstallmentPaymentMarker.SetPaidState(payment, true, new DateOnly(2026, 6, 20));

        Assert.True(payment.IsPaid);
        Assert.Equal(new DateOnly(2026, 6, 20), payment.PaidDate);
    }

    /// <summary>Verifies setting paid without a date is rejected before persistence.</summary>
    [Fact]
    public void SetPaidState_RequiresPaidDateWhenMarkingPaid()
    {
        var payment = new InstallmentPayment { IsPaid = false };

        var error = Assert.Throws<ArgumentException>(() => InstallmentPaymentMarker.SetPaidState(payment, true, null));

        Assert.Equal("請選擇實際繳款日", error.Message);
    }

    /// <summary>Verifies setting an unpaid target state clears a paid date.</summary>
    [Fact]
    public void SetPaidState_ClearsPaidDateWhenSettingUnpaid()
    {
        var payment = new InstallmentPayment
        {
            IsPaid = true,
            PaidDate = new DateOnly(2026, 6, 20),
        };

        InstallmentPaymentMarker.SetPaidState(payment, false, null);

        Assert.False(payment.IsPaid);
        Assert.Null(payment.PaidDate);
    }

    /// <summary>Verifies repeating a target state does not toggle or mutate the payment.</summary>
    [Fact]
    public void SetPaidState_IsIdempotentForMatchingTarget()
    {
        var paidDate = new DateOnly(2026, 6, 20);
        var payment = new InstallmentPayment { IsPaid = true, PaidDate = paidDate };

        InstallmentPaymentMarker.SetPaidState(payment, true, paidDate);

        Assert.True(payment.IsPaid);
        Assert.Equal(paidDate, payment.PaidDate);
    }

    /// <summary>Verifies repeating an unpaid target state remains a no-op.</summary>
    [Fact]
    public void SetPaidState_IsIdempotentForUnpaidTarget()
    {
        var payment = new InstallmentPayment { IsPaid = false, PaidDate = null };

        InstallmentPaymentMarker.SetPaidState(payment, false, null);

        Assert.False(payment.IsPaid);
        Assert.Null(payment.PaidDate);
    }
}
