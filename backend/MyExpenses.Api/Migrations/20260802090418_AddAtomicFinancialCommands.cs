using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAtomicFinancialCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstallmentPayments_InstallmentId",
                table: "InstallmentPayments");

            migrationBuilder.DropColumn(
                name: "RemainingPeriods",
                table: "Installments");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Installments");

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TransactionId = table.Column<int>(type: "INTEGER", nullable: true),
                    InstallmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPayments_InstallmentId_IsPaid",
                table: "InstallmentPayments",
                columns: new[] { "InstallmentId", "IsPaid" });

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPayments_InstallmentId_Period",
                table: "InstallmentPayments",
                columns: new[] { "InstallmentId", "Period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_Key",
                table: "IdempotencyRecords",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_Operation_RequestHash",
                table: "IdempotencyRecords",
                columns: new[] { "Operation", "RequestHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropIndex(
                name: "IX_InstallmentPayments_InstallmentId_IsPaid",
                table: "InstallmentPayments");

            migrationBuilder.DropIndex(
                name: "IX_InstallmentPayments_InstallmentId_Period",
                table: "InstallmentPayments");

            migrationBuilder.AddColumn<int>(
                name: "RemainingPeriods",
                table: "Installments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Installments",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPayments_InstallmentId",
                table: "InstallmentPayments",
                column: "InstallmentId");
        }
    }
}
