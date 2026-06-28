namespace MyExpenses.Api.Models;

public class InstallmentPayment
{
    public int Id { get; set; }
    public int InstallmentId { get; set; }
    public int Period { get; set; }
    public decimal Amount { get; set; }
    public DateTime? PaidDate { get; set; }
    public bool IsPaid { get; set; }
    /// <summary>繳款截止日（不包含時間）</summary>
    public DateOnly? DueDate { get; set; }

    public Installment Installment { get; set; } = null!;
}
