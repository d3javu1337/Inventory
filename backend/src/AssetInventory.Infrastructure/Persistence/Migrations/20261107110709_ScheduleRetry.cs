using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "inventory_schedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetryAt",
                table: "inventory_schedules",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "inventory_schedules");

            migrationBuilder.DropColumn(
                name: "RetryAt",
                table: "inventory_schedules");
        }
    }
}
