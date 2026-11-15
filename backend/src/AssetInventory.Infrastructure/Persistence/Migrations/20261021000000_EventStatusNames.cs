using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261021000000_EventStatusNames")]
public partial class EventStatusNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE integration_events SET \"Status\" = 'Accepted' WHERE \"Status\" = 'Pending';");
        migrationBuilder.Sql("UPDATE integration_events SET \"Status\" = 'Processed' WHERE \"Status\" = 'Completed';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE integration_events SET \"Status\" = 'Pending' WHERE \"Status\" = 'Accepted';");
        migrationBuilder.Sql("UPDATE integration_events SET \"Status\" = 'Completed' WHERE \"Status\" = 'Processed';");
    }
}
