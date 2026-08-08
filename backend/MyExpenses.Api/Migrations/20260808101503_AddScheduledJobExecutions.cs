using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyExpenses.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledJobExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledJobExecutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ScheduledForUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScheduleTimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ScheduledLocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SucceededCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AffectedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultCode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    SafeMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobExecutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobExecutions_JobKey_ScheduledForUtc",
                table: "ScheduledJobExecutions",
                columns: new[] { "JobKey", "ScheduledForUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobExecutions_JobKey_StartedAtUtc",
                table: "ScheduledJobExecutions",
                columns: new[] { "JobKey", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobExecutions_StartedAtUtc",
                table: "ScheduledJobExecutions",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobExecutions_Status_StartedAtUtc",
                table: "ScheduledJobExecutions",
                columns: new[] { "Status", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledJobExecutions");
        }
    }
}
