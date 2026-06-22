namespace MyExpenses.Api.Models;

public class Stock
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal Shares { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public string? Broker { get; set; }
    public DateTime? LastPriceUpdate { get; set; }
}
