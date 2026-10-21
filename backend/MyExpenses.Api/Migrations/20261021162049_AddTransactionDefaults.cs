using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionDefaults : Migration
    {
        /// <summary>新增可空的固定預設參照，成功刪除資源時由外鍵原子清空。</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultBankAccountId",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultCreditCardId",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_DefaultBankAccountId",
                table: "SystemSettings",
                column: "DefaultBankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_DefaultCreditCardId",
                table: "SystemSettings",
                column: "DefaultCreditCardId");

            migrationBuilder.AddForeignKey(
                name: "FK_SystemSettings_BankAccounts_DefaultBankAccountId",
                table: "SystemSettings",
                column: "DefaultBankAccountId",
                principalTable: "BankAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_SystemSettings_CreditCards_DefaultCreditCardId",
                table: "SystemSettings",
                column: "DefaultCreditCardId",
                principalTable: "CreditCards",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <summary>移除本次新增的預設參照，不改動既有金融資料。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SystemSettings_BankAccounts_DefaultBankAccountId",
                table: "SystemSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_SystemSettings_CreditCards_DefaultCreditCardId",
                table: "SystemSettings");

            migrationBuilder.DropIndex(
                name: "IX_SystemSettings_DefaultBankAccountId",
                table: "SystemSettings");

            migrationBuilder.DropIndex(
                name: "IX_SystemSettings_DefaultCreditCardId",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "DefaultBankAccountId",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "DefaultCreditCardId",
                table: "SystemSettings");
        }
    }
}
