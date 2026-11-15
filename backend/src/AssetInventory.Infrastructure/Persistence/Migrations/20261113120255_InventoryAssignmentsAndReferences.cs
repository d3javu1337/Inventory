using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryAssignmentsAndReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RenameAssignments(migrationBuilder, "commission_members", "inventory_assignments");

            migrationBuilder.AddForeignKey(
                name: "fk_asset_assignment_history_assets_asset_id",
                table: "asset_assignment_history",
                column: "asset_id",
                principalTable: "assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_documents_inventories_inventory_id",
                table: "generated_documents",
                column: "inventory_id",
                principalTable: "inventories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_unexpected_scans_inventories_inventory_id",
                table: "unexpected_scans",
                column: "inventory_id",
                principalTable: "inventories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_asset_assignment_history_assets_asset_id",
                table: "asset_assignment_history");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_documents_inventories_inventory_id",
                table: "generated_documents");

            migrationBuilder.DropForeignKey(
                name: "fk_unexpected_scans_inventories_inventory_id",
                table: "unexpected_scans");

            RenameAssignments(migrationBuilder, "inventory_assignments", "commission_members");
        }

        private static void RenameAssignments(MigrationBuilder migrationBuilder, string from, string to)
        {
            migrationBuilder.RenameTable(from, newName: to);
            migrationBuilder.RenameIndex($"ix_{from}_inventory_id", $"ix_{to}_inventory_id", to);
            migrationBuilder.RenameIndex($"ix_{from}_user_id", $"ix_{to}_user_id", to);
            migrationBuilder.Sql($"""
                ALTER TABLE {to} RENAME CONSTRAINT pk_{from} TO pk_{to};
                ALTER TABLE {to} RENAME CONSTRAINT fk_{from}_inventories_inventory_id TO fk_{to}_inventories_inventory_id;
                ALTER TABLE {to} RENAME CONSTRAINT fk_{from}_user_accounts_user_id TO fk_{to}_user_accounts_user_id;
                """);
        }
    }
}
