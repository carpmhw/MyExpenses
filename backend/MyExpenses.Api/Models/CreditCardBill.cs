namespace MyExpenses.Api.Models;

public class CreditCardBill
{
    public int Id { get; set; }
    public int CardId { get; set; }
    public string Period { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    /// <summary>繳款截止日（不包含時間）</summary>
    public DateOnly DueDate { get; set; }
    public bool IsPaid { get; set; }

    public CreditCard Card { get; set; } = null!;
}
