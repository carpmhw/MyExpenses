namespace MyExpenses.Api.Models;

public class CreditCard
{
    public int Id { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string LastFourDigits { get; set; } = string.Empty;
    public int StatementDay { get; set; }
    public int DueDay { get; set; }
    public decimal CreditLimit { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CreditCardBill> Bills { get; set; } = new List<CreditCardBill>();
}
