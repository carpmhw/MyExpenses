using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotNetWorthBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NetWorthBasis",
                table: "SnapshotBatches",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "AssetsOnly");

            migrationBuilder.AddColumn<decimal>(
                name: "TotalAssets",
                table: "SnapshotBatches",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalLiabilities",
                table: "SnapshotBatches",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE SnapshotBatches SET TotalAssets = TotalNetWorth, NetWorthBasis = 'AssetsOnly';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NetWorthBasis",
                table: "SnapshotBatches");

            migrationBuilder.DropColumn(
                name: "TotalAssets",
                table: "SnapshotBatches");

            migrationBuilder.DropColumn(
                name: "TotalLiabilities",
                table: "SnapshotBatches");
        }
    }
}
