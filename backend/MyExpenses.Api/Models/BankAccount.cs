using System.ComponentModel.DataAnnotations;

namespace MyExpenses.Api.Models;

public class BankAccount
{
    public int Id { get; set; }
    [Required]
    public string BankName { get; set; } = string.Empty;
    [Required(ErrorMessage = "請填寫帳號後五碼")]
    [RegularExpression(@"^\d{5}$", ErrorMessage = "帳號後五碼必須為 5 位數字")]
    public string AccountNumber { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string AccountType { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
