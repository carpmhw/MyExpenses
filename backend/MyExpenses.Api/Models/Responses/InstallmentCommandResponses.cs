using MyExpenses.Api.Models;

namespace MyExpenses.Api.Models.Responses;

/// <summary>Returns the canonical aggregate created by an installment purchase command.</summary>
public sealed record InstallmentPurchaseResponse(
    TransactionCommandResponse Transaction,
    InstallmentCommandResponse Installment);

/// <summary>Returns the canonical installment aggregate after a command completes.</summary>
public sealed record InstallmentCommandResponse(
    int Id,
    int? TransactionId,
    int? CardId,
    decimal TotalAmount,
    int Periods,
    decimal PerPeriod,
    int RemainingPeriods,
    DateOnly PurchaseDate,
    DateTime CreatedAt,
    InstallmentStatus Status,
    string? Description,
    TransactionCommandResponse? Transaction,
    CreditCardCommandResponse? Card,
    IReadOnlyList<InstallmentPaymentCommandResponse> Payments);

/// <summary>Returns only non-cyclic transaction fields needed by an installment command response.</summary>
public sealed record TransactionCommandResponse(
    int Id,
    TransactionType Type,
    decimal Amount,
    DateOnly Date,
    string? Description,
    string? Notes,
    int CategoryId,
    int? PaymentMethodId,
    DateTime CreatedAt);

/// <summary>Returns only non-cyclic credit-card fields needed by an installment command response.</summary>
public sealed record CreditCardCommandResponse(
    int Id,
    string BankName,
    string LastFourDigits,
    string? CardNetwork,
    int StatementDay,
    int DueDay,
    decimal CreditLimit);

/// <summary>Returns one payment schedule row without its parent navigation property.</summary>
public sealed record InstallmentPaymentCommandResponse(
    int Id,
    int InstallmentId,
    int Period,
    decimal Amount,
    DateOnly? PaidDate,
    bool IsPaid,
    DateOnly? DueDate);
