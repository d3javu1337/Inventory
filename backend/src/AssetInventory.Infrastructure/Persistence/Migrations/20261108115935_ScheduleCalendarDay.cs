using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleCalendarDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DayOfMonth",
                table: "inventory_schedules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inventory_schedules_DayOfMonth",
                table: "inventory_schedules",
                sql: "\"DayOfMonth\" BETWEEN 1 AND 31");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_inventory_schedules_DayOfMonth",
                table: "inventory_schedules");

            migrationBuilder.DropColumn(
                name: "DayOfMonth",
                table: "inventory_schedules");
        }
    }
}
