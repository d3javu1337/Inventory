using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SingleInventoryAssignee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM commission_members AS assignment
                USING (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "InventoryId"
                               ORDER BY CASE WHEN "Role" = 'Chairperson' THEN 0 ELSE 1 END, "Id"
                           ) AS row_number
                    FROM commission_members
                ) AS duplicate
                WHERE assignment."Id" = duplicate."Id"
                  AND duplicate.row_number > 1;
                """);

            migrationBuilder.Sql(
                """
                UPDATE inventories AS inventory
                SET "Status" = 'Completed',
                    "CompletedAt" = COALESCE(inventory."CompletedAt", CURRENT_TIMESTAMP)
                FROM commission_members AS assignment
                WHERE assignment."InventoryId" = inventory."Id"
                  AND assignment."ConfirmationStatus" = 'Confirmed'
                  AND inventory."Status" = 'AwaitingConfirmation';
                """);

            migrationBuilder.DropIndex(
                name: "IX_commission_members_InventoryId_UserId",
                table: "commission_members");

            migrationBuilder.CreateIndex(
                name: "IX_commission_members_InventoryId",
                table: "commission_members",
                column: "InventoryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_commission_members_InventoryId",
                table: "commission_members");

            migrationBuilder.CreateIndex(
                name: "IX_commission_members_InventoryId_UserId",
                table: "commission_members",
                columns: new[] { "InventoryId", "UserId" },
                unique: true);
        }
    }
}
