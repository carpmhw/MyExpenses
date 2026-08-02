namespace MyExpenses.Api.Models;

/// <summary>Stores the committed identity and result of an idempotent financial create command.</summary>
public sealed class IdempotencyRecord
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int? TransactionId { get; set; }
    public int? InstallmentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
