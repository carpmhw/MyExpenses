namespace MyExpenses.Api.Models;

public enum InstallmentStatus
{
    Active,
    PaidOff
}

public class Installment
{
    public int Id { get; set; }
    public int? TransactionId { get; set; }
    public int? CardId { get; set; }
    public decimal TotalAmount { get; set; }
    public int Periods { get; set; }
    public decimal PerPeriod { get; set; }
    public int RemainingPeriods { get; set; }
    /// <summary>刷卡日期（不包含時間）</summary>
    public DateOnly PurchaseDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public InstallmentStatus Status { get; set; } = InstallmentStatus.Active;
    public string? Description { get; set; }

    public Transaction? Transaction { get; set; }
    public CreditCard? Card { get; set; }
    public ICollection<InstallmentPayment> Payments { get; set; } = new List<InstallmentPayment>();
}
