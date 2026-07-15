using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class UnifySystemTimezone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            // SQLite stores both DateTime and DateOnly as TEXT; keep the existing calendar date and drop the meaningless time.
            migrationBuilder.Sql(
                "UPDATE InstallmentPayments SET PaidDate = substr(PaidDate, 1, 10) WHERE PaidDate IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE InstallmentPayments SET PaidDate = PaidDate || ' 00:00:00' WHERE PaidDate IS NOT NULL;");
            migrationBuilder.DropTable(
                name: "SystemSettings");
        }
    }
}
