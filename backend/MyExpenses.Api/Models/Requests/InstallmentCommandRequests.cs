namespace MyExpenses.Api.Models.Requests;

/// <summary>Combines an expense transaction request with the installment-only fields.</summary>
public sealed class InstallmentPurchaseRequest
{
    public CreateTransactionRequest Transaction { get; set; } = new();
    public InstallmentPurchaseDetails Installment { get; set; } = new();
}

/// <summary>Contains fields that are unique to an installment purchase command.</summary>
public sealed class InstallmentPurchaseDetails
{
    public int CardId { get; set; }
    public int Periods { get; set; }
}

/// <summary>Describes a standalone installment creation request.</summary>
public sealed class CreateStandaloneInstallmentRequest
{
    public int? TransactionId { get; set; }
    public int? CardId { get; set; }
    public decimal TotalAmount { get; set; }
    public int Periods { get; set; }
    public DateOnly PurchaseDate { get; set; }
    public string? Description { get; set; }
}

/// <summary>Describes schedule fields that may be changed before any payment is paid.</summary>
public sealed class UpdateInstallmentScheduleRequest
{
    public int? CardId { get; set; }
    public decimal TotalAmount { get; set; }
    public int Periods { get; set; }
    public DateOnly PurchaseDate { get; set; }
    public string? Description { get; set; }
}

/// <summary>Requests an explicit target state for one installment payment.</summary>
public sealed class SetInstallmentPaymentStateRequest
{
    public bool? IsPaid { get; set; }
    public DateOnly? PaidDate { get; set; }
}
