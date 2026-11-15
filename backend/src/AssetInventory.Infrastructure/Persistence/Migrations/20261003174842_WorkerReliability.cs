using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkerReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ScheduledPeriod",
                table: "inventories",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceScheduleId",
                table: "inventories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "integration_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventories_SourceScheduleId_ScheduledPeriod",
                table: "inventories",
                columns: new[] { "SourceScheduleId", "ScheduledPeriod" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_events_Status_NextAttemptAt",
                table: "integration_events",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_inventories_SourceScheduleId_ScheduledPeriod",
                table: "inventories");

            migrationBuilder.DropIndex(
                name: "IX_integration_events_Status_NextAttemptAt",
                table: "integration_events");

            migrationBuilder.DropColumn(
                name: "ScheduledPeriod",
                table: "inventories");

            migrationBuilder.DropColumn(
                name: "SourceScheduleId",
                table: "inventories");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "integration_events");
        }
    }
}
