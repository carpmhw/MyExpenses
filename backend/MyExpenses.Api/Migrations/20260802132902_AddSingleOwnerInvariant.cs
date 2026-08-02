using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSingleOwnerInvariant : Migration
    {
        /// <summary>套用 single-owner marker、unique index 與 check constraint。</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InstallationOwnerMarker",
                table: "Users",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "myexpenses-owner");

            migrationBuilder.CreateIndex(
                name: "IX_Users_InstallationOwnerMarker",
                table: "Users",
                column: "InstallationOwnerMarker",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_InstallationOwnerMarker",
                table: "Users",
                sql: "InstallationOwnerMarker = 'myexpenses-owner'");
        }

        /// <summary>移除 single-owner marker 與其 database constraints。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_InstallationOwnerMarker",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_InstallationOwnerMarker",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "InstallationOwnerMarker",
                table: "Users");
        }
    }
}
