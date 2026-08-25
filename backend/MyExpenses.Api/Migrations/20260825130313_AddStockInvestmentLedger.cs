using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStockInvestmentLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Close",
                table: "HistoricalAdjustedPrices",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StockTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StockId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TradeDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Shares = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Fee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CashAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    OpeningMarketValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransactions", x => x.Id);
                    table.CheckConstraint("CK_StockTransactions_FeeTax_NonNegative", "Fee >= 0 AND Tax >= 0");
                    table.CheckConstraint("CK_StockTransactions_TypeFields", "(Type = 'OpeningBalance' AND Shares > 0 AND Price > 0 AND OpeningMarketValue > 0 AND CashAmount IS NULL) OR (Type IN ('Buy', 'Sell') AND Shares > 0 AND Price > 0 AND OpeningMarketValue IS NULL AND CashAmount IS NULL) OR (Type = 'Dividend' AND CashAmount > 0 AND Shares IS NULL AND Price IS NULL AND OpeningMarketValue IS NULL)");
                    table.ForeignKey(
                        name: "FK_StockTransactions_Stocks_StockId",
                        column: x => x.StockId,
                        principalTable: "Stocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_HistoricalAdjustedPrices_Close_Positive",
                table: "HistoricalAdjustedPrices",
                sql: "Close IS NULL OR Close > 0");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_StockId_TradeDate_Sequence",
                table: "StockTransactions",
                columns: new[] { "StockId", "TradeDate", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_StockId_Type_TradeDate",
                table: "StockTransactions",
                columns: new[] { "StockId", "Type", "TradeDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HistoricalAdjustedPrices_Close_Positive",
                table: "HistoricalAdjustedPrices");

            migrationBuilder.DropColumn(
                name: "Close",
                table: "HistoricalAdjustedPrices");
        }
    }
}
