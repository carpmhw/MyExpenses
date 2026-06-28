using System.ComponentModel.DataAnnotations;

namespace MyExpenses.Api.Models.Requests;

public class CreateTransactionRequest
{
    public TransactionType? Type { get; set; }
    [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be greater than 0")]
    public decimal Amount { get; set; }
    /// <summary>交易日期</summary>
    public DateOnly? Date { get; set; }
    [Required]
    public string Description { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
    public string? CategoryCode { get; set; }
    public string? Category { get; set; }
    public string? Notes { get; set; }
    public int? PaymentMethodId { get; set; }
    public string? PaymentMethod { get; set; }
    public string? PaymentMethodCode { get; set; }
}
