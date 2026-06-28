namespace MyExpenses.Api.Models;

public enum TransactionType
{
    Income,
    Expense
}

public class Transaction
{
    public int Id { get; set; }
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    /// <summary>交易日期（不包含時間）</summary>
    public DateOnly Date { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public int CategoryId { get; set; }
    public int? PaymentMethodId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public Category Category { get; set; } = null!;
    public PaymentMethod? PaymentMethod { get; set; }
}
