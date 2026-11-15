using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryFormSnapshotFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                table: "inventory_asset_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "inventory_asset_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "manufacture_year",
                table: "inventory_asset_snapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "materially_responsible_employee_id",
                table: "inventory_asset_snapshots",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "department_id",
                table: "inventory_asset_snapshots");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "inventory_asset_snapshots");

            migrationBuilder.DropColumn(
                name: "manufacture_year",
                table: "inventory_asset_snapshots");

            migrationBuilder.DropColumn(
                name: "materially_responsible_employee_id",
                table: "inventory_asset_snapshots");
        }
    }
}
