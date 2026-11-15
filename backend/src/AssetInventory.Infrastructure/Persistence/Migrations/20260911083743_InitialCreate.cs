using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asset_assignment_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MateriallyResponsibleEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_assignment_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: true),
                    DataJson = table.Column<string>(type: "jsonb", nullable: true),
                    IpAddress = table.Column<string>(type: "text", nullable: true),
                    CorrelationId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    IsInventoryDepartment = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "generated_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StorageKey = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "integration_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedInventoryId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inventories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReasonDetails = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlannedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScanningFinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inventory_schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IntervalMonths = table.Column<int>(type: "integer", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LaunchMode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Inn = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Kpp = table.Column<string>(type: "text", nullable: true),
                    Okpo = table.Column<string>(type: "text", nullable: true),
                    Address = table.Column<string>(type: "text", nullable: true),
                    DirectorName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "unexpected_scans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawValue = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RecognizedAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScannedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unexpected_scans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "employees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonnelNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FullName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsMateriallyResponsible = table.Column<bool>(type: "boolean", nullable: false),
                    IsEmployed = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_employees_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_locations_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_asset_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryNumber = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AccountingAccount = table.Column<string>(type: "text", nullable: false),
                    SerialNumber = table.Column<string>(type: "text", nullable: true),
                    AcceptedAt = table.Column<DateOnly>(type: "date", nullable: false),
                    InitialCost = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    DepartmentName = table.Column<string>(type: "text", nullable: false),
                    LocationName = table.Column<string>(type: "text", nullable: false),
                    MateriallyResponsibleFullName = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Condition = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    ScannedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScannedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_asset_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_inventory_asset_snapshots_inventories_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "inventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Login = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedLogin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_accounts_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AccountingAccount = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SerialNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ManufactureYear = table.Column<int>(type: "integer", nullable: true),
                    AcceptedAt = table.Column<DateOnly>(type: "date", nullable: false),
                    InitialCost = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MateriallyResponsibleEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assets_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assets_employees_MateriallyResponsibleEmployeeId",
                        column: x => x.MateriallyResponsibleEmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assets_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConfirmationStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_commission_members_inventories_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "inventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_commission_members_user_accounts_UserId",
                        column: x => x.UserId,
                        principalTable: "user_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Device = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_user_accounts_UserId",
                        column: x => x.UserId,
                        principalTable: "user_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_assignment_history_AssetId_ValidFrom",
                table: "asset_assignment_history",
                columns: new[] { "AssetId", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_DepartmentId",
                table: "assets",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_assets_InventoryNumber",
                table: "assets",
                column: "InventoryNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_LocationId",
                table: "assets",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_assets_MateriallyResponsibleEmployeeId_Status",
                table: "assets",
                columns: new[] { "MateriallyResponsibleEmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CreatedAt",
                table: "audit_logs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_EntityType_EntityId",
                table: "audit_logs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_members_InventoryId_UserId",
                table: "commission_members",
                columns: new[] { "InventoryId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commission_members_UserId",
                table: "commission_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_departments_Code",
                table: "departments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employees_DepartmentId",
                table: "employees",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_employees_PersonnelNumber",
                table: "employees",
                column: "PersonnelNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generated_documents_InventoryId_Type",
                table: "generated_documents",
                columns: new[] { "InventoryId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_events_ExternalEventId",
                table: "integration_events",
                column: "ExternalEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_events_Status_ReceivedAt",
                table: "integration_events",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_inventories_Number",
                table: "inventories",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventories_Status_PlannedAt",
                table: "inventories",
                columns: new[] { "Status", "PlannedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_asset_snapshots_InventoryId_AssetId",
                table: "inventory_asset_snapshots",
                columns: new[] { "InventoryId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_schedules_IsEnabled_NextRunAt",
                table: "inventory_schedules",
                columns: new[] { "IsEnabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_locations_DepartmentId_Code",
                table: "locations",
                columns: new[] { "DepartmentId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Inn",
                table: "organizations",
                column: "Inn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_unexpected_scans_InventoryId_ScannedAt",
                table: "unexpected_scans",
                columns: new[] { "InventoryId", "ScannedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_user_accounts_EmployeeId",
                table: "user_accounts",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_user_accounts_NormalizedLogin",
                table: "user_accounts",
                column: "NormalizedLogin",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_assignment_history");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "commission_members");

            migrationBuilder.DropTable(
                name: "generated_documents");

            migrationBuilder.DropTable(
                name: "integration_events");

            migrationBuilder.DropTable(
                name: "inventory_asset_snapshots");

            migrationBuilder.DropTable(
                name: "inventory_schedules");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "unexpected_scans");

            migrationBuilder.DropTable(
                name: "locations");

            migrationBuilder.DropTable(
                name: "inventories");

            migrationBuilder.DropTable(
                name: "user_accounts");

            migrationBuilder.DropTable(
                name: "employees");

            migrationBuilder.DropTable(
                name: "departments");
        }
    }
}
