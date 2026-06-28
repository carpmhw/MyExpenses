namespace MyExpenses.Api.Models;

public class Withdrawal
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    /// <summary>提領日期（不包含時間）</summary>
    public DateOnly Date { get; set; }
    public string? Description { get; set; }
    public int BankAccountId { get; set; }

    public BankAccount BankAccount { get; set; } = null!;
}
