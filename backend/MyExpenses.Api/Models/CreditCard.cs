using System.ComponentModel.DataAnnotations;

namespace MyExpenses.Api.Models;

public class CreditCard : IValidatableObject
{
    private static readonly HashSet<string> ValidCardNetworks = new(StringComparer.Ordinal)
    {
        "VISA",
        "Mastercard",
        "JCB",
        "American Express",
        "UnionPay",
        "其他",
    };

    public int Id { get; set; }
    [Required]
    public string BankName { get; set; } = string.Empty;
    [Required(ErrorMessage = "請填寫卡號後四碼")]
    [RegularExpression(@"^\d{4}$", ErrorMessage = "卡號後四碼必須為 4 位數字")]
    public string LastFourDigits { get; set; } = string.Empty;
    [StringLength(50, ErrorMessage = "卡種最多 50 字元")]
    public string? CardNetwork { get; set; }
    public int StatementDay { get; set; }
    public int DueDay { get; set; }
    public decimal CreditLimit { get; set; }
    [StringLength(200, ErrorMessage = "備註最多 200 字元")]
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CreditCardBill> Bills { get; set; } = new List<CreditCardBill>();

    /// <summary>Validates optional credit card metadata against supported card network values.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var normalizedCardNetwork = CardNetwork?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedCardNetwork) && !ValidCardNetworks.Contains(normalizedCardNetwork))
        {
            yield return new ValidationResult("卡種必須為有效的國際卡別網路", [nameof(CardNetwork)]);
        }
    }
}
