using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStockMarketRiskData : Migration
    {
        /// <summary>新增持股市場、歷史還原價格與同步狀態資料表。</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Market",
                table: "Stocks",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.CreateTable(
                name: "HistoricalAdjustedPrices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Market = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TradingDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AdjustedClose = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalAdjustedPrices", x => x.Id);
                    table.CheckConstraint("CK_HistoricalAdjustedPrices_AdjustedClose_Positive", "AdjustedClose > 0");
                });

            migrationBuilder.CreateTable(
                name: "HistoricalPriceSyncStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Market = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LastAttemptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSucceededAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LatestTradingDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SafeMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalPriceSyncStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalAdjustedPrices_Market_Symbol_TradingDate",
                table: "HistoricalAdjustedPrices",
                columns: new[] { "Market", "Symbol", "TradingDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalPriceSyncStates_Market_Symbol",
                table: "HistoricalPriceSyncStates",
                columns: new[] { "Market", "Symbol" },
                unique: true);
        }

        /// <summary>移除市場風險相關欄位、資料表與索引。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricalAdjustedPrices");

            migrationBuilder.DropTable(
                name: "HistoricalPriceSyncStates");

            migrationBuilder.DropColumn(
                name: "Market",
                table: "Stocks");
        }
    }
}
