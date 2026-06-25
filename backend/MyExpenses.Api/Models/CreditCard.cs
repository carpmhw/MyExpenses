using System.ComponentModel.DataAnnotations;

namespace MyExpenses.Api.Models;

public class CreditCard
{
    public int Id { get; set; }
    [Required]
    public string BankName { get; set; } = string.Empty;
    [Required(ErrorMessage = "請填寫卡號後四碼")]
    [RegularExpression(@"^\d{4}$", ErrorMessage = "卡號後四碼必須為 4 位數字")]
    public string LastFourDigits { get; set; } = string.Empty;
    public int StatementDay { get; set; }
    public int DueDay { get; set; }
    public decimal CreditLimit { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CreditCardBill> Bills { get; set; } = new List<CreditCardBill>();
}
