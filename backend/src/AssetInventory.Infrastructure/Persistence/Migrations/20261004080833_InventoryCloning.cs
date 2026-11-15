using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryCloning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceInventoryId",
                table: "inventories",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventories_SourceInventoryId",
                table: "inventories",
                column: "SourceInventoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_inventories_inventories_SourceInventoryId",
                table: "inventories",
                column: "SourceInventoryId",
                principalTable: "inventories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_inventories_inventories_SourceInventoryId",
                table: "inventories");

            migrationBuilder.DropIndex(
                name: "IX_inventories_SourceInventoryId",
                table: "inventories");

            migrationBuilder.DropColumn(
                name: "SourceInventoryId",
                table: "inventories");
        }
    }
}
