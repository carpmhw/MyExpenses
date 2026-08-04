using System.ComponentModel.DataAnnotations.Schema;

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
    private int? _remainingPeriodsFallback;
    private InstallmentStatus? _statusFallback;

    /// <summary>Returns the number of unpaid payment records without persisting a duplicate summary value.</summary>
    [NotMapped]
    public int RemainingPeriods
    {
        get => Payments.Count > 0
            ? Payments.Count(payment => !payment.IsPaid)
            : _remainingPeriodsFallback ?? Math.Max(Periods, 0);
        set => _remainingPeriodsFallback = value;
    }

    /// <summary>刷卡日期（不包含時間）</summary>
    public DateOnly PurchaseDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Returns the lifecycle status derived from the current payment records.</summary>
    [NotMapped]
    public InstallmentStatus Status
    {
        get => Payments.Count > 0
            ? RemainingPeriods == 0 ? InstallmentStatus.PaidOff : InstallmentStatus.Active
            : _statusFallback ?? (RemainingPeriods == 0 ? InstallmentStatus.PaidOff : InstallmentStatus.Active);
        set => _statusFallback = value;
    }
    public string? Description { get; set; }

    public Transaction? Transaction { get; set; }
    public CreditCard? Card { get; set; }
    public ICollection<InstallmentPayment> Payments { get; set; } = new List<InstallmentPayment>();
}
