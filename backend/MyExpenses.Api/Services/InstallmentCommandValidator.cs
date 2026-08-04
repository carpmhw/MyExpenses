using System.Net;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>Centralizes cross-field validation shared by installment financial commands.</summary>
public static class InstallmentCommandValidator
{
    /// <summary>Validates amount, period count, and date-only schedule fields.</summary>
    public static void ValidateSchedule(decimal totalAmount, int periods, DateOnly purchaseDate)
    {
        if (totalAmount <= 0)
            throw ValidationError("總金額必須大於零");
        if (periods <= 1)
            throw ValidationError("期數必須大於 1");
        if (purchaseDate == default)
            throw ValidationError("請選擇刷卡日期");
    }

    /// <summary>Ensures the category belongs to the expense side of the ledger.</summary>
    public static void ValidateExpenseCategory(Category category)
    {
        if (category.Type != CategoryType.Expense)
            throw SemanticError("分期消費必須使用支出分類");
    }

    /// <summary>Ensures the payment method represents a credit-card purchase.</summary>
    public static void ValidateCreditCardPaymentMethod(PaymentMethod paymentMethod)
    {
        if (!string.Equals(paymentMethod.SystemCode, "credit-card", StringComparison.OrdinalIgnoreCase))
            throw SemanticError("分期消費必須使用信用卡支付方式");
    }

    /// <summary>Creates a malformed-input command exception.</summary>
    private static FinancialCommandException ValidationError(string detail)
        => new((int)HttpStatusCode.BadRequest, "Invalid financial command", detail);

    /// <summary>Creates an incompatible-resource command exception.</summary>
    private static FinancialCommandException SemanticError(string detail)
        => new((int)HttpStatusCode.UnprocessableEntity, "Invalid financial relationship", detail);
}
