namespace MyExpenses.Api.Models;

public class PaymentMethod
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string Color { get; set; } = "#6B7280";
    public string? SystemCode { get; set; }
}
