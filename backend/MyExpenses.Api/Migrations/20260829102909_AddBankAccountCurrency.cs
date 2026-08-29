using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBankAccountCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExchangeRateIsStale",
                table: "SnapshotBatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExchangeRateUpdatedAt",
                table: "SnapshotBatches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "BankAccounts",
                type: "TEXT",
                maxLength: 3,
                nullable: false,
                defaultValue: "TWD");

            migrationBuilder.Sql(
                "UPDATE SnapshotBatches "
                + "SET BankDetails = ("
                 + "SELECT COALESCE(json_group_array(json_set(json(value), "
                 + "'$.CurrencyCode', 'TWD', "
                 + "'$.ExchangeRate', printf('%s', '1'), "
                 + "'$.BaseCurrencyCode', 'TWD', "
                 + "'$.Balance', printf('%s', COALESCE(json_extract(value, '$.Balance'), json_extract(value, '$.balance'), 0)), "
                 + "'$.ConvertedBalance', printf('%s', COALESCE(json_extract(value, '$.Balance'), json_extract(value, '$.balance'), 0)))), '[]') "
                + "FROM json_each(CASE "
                + "WHEN json_valid(COALESCE(BankDetails, '[]')) THEN COALESCE(BankDetails, '[]') "
                + "ELSE '[]' END));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExchangeRateIsStale",
                table: "SnapshotBatches");

            migrationBuilder.DropColumn(
                name: "ExchangeRateUpdatedAt",
                table: "SnapshotBatches");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "BankAccounts");
        }
    }
}
