namespace MyExpenses.Api.Models;

public enum CategoryType
{
    Income,
    Expense
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CategoryType Type { get; set; }
    public string Icon { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? SystemCode { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
