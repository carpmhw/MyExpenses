using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class InstallmentPaymentMarkerTests
{
    /// <summary>Verifies marking an unpaid period paid stores the user-provided paid date.</summary>
    [Fact]
    public void TogglePaid_StoresProvidedPaidDateWhenMarkingPaid()
    {
        var payment = new InstallmentPayment { IsPaid = false };

        InstallmentPaymentMarker.TogglePaid(payment, new DateOnly(2026, 6, 20));

        Assert.True(payment.IsPaid);
        Assert.Equal(new DateTime(2026, 6, 20), payment.PaidDate);
    }

    /// <summary>Verifies marking paid without a date is rejected before persistence.</summary>
    [Fact]
    public void TogglePaid_RequiresPaidDateWhenMarkingPaid()
    {
        var payment = new InstallmentPayment { IsPaid = false };

        var error = Assert.Throws<ArgumentException>(() => InstallmentPaymentMarker.TogglePaid(payment, null));

        Assert.Equal("請選擇實際繳款日", error.Message);
    }

    /// <summary>Verifies canceling a paid period clears the stored paid date.</summary>
    [Fact]
    public void TogglePaid_ClearsPaidDateWhenCancelingPaidStatus()
    {
        var payment = new InstallmentPayment
        {
            IsPaid = true,
            PaidDate = new DateTime(2026, 6, 20),
        };

        InstallmentPaymentMarker.TogglePaid(payment, null);

        Assert.False(payment.IsPaid);
        Assert.Null(payment.PaidDate);
    }
}
