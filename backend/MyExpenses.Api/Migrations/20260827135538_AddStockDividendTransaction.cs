using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStockDividendTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransactions_TypeFields",
                table: "StockTransactions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransactions_TypeFields",
                table: "StockTransactions",
                sql: "(Type = 'OpeningBalance' AND Shares > 0 AND Price > 0 AND OpeningMarketValue > 0 AND CashAmount IS NULL) OR (Type IN ('Buy', 'Sell') AND Shares > 0 AND Price > 0 AND OpeningMarketValue IS NULL AND CashAmount IS NULL) OR (Type = 'Dividend' AND CashAmount > 0 AND Shares IS NULL AND Price IS NULL AND OpeningMarketValue IS NULL) OR (Type = 'StockDividend' AND Shares IS NOT NULL AND Shares > 0 AND Price IS NULL AND CashAmount IS NULL AND OpeningMarketValue IS NULL AND Fee = 0 AND Tax = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransactions_TypeFields",
                table: "StockTransactions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransactions_TypeFields",
                table: "StockTransactions",
                sql: "(Type = 'OpeningBalance' AND Shares > 0 AND Price > 0 AND OpeningMarketValue > 0 AND CashAmount IS NULL) OR (Type IN ('Buy', 'Sell') AND Shares > 0 AND Price > 0 AND OpeningMarketValue IS NULL AND CashAmount IS NULL) OR (Type = 'Dividend' AND CashAmount > 0 AND Shares IS NULL AND Price IS NULL AND OpeningMarketValue IS NULL)");
        }
    }
}
