namespace MyExpenses.Api.Models;

/// <summary>描述股票 Ledger 可重播的交易類型。</summary>
public enum StockTransactionType
{
    OpeningBalance,
    Buy,
    Sell,
    Dividend,
}
