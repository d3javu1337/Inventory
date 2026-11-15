using AssetInventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetInventory.Infrastructure.Persistence;

internal abstract class EntityConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : Entity
{
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.HasKey(entity => entity.Id);
    }
}

internal sealed class OrganizationConfiguration : EntityConfiguration<Organization>
{
    public override void Configure(EntityTypeBuilder<Organization> builder)
    {
        base.Configure(builder);
        builder.ToTable("organizations");
        builder.Property(x => x.Name).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Inn).HasMaxLength(12).IsRequired();
        builder.HasIndex(x => x.Inn).IsUnique();
    }
}

internal sealed class DepartmentConfiguration : EntityConfiguration<Department>
{
    public override void Configure(EntityTypeBuilder<Department> builder)
    {
        base.Configure(builder);
        builder.ToTable("departments");
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(250).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class LocationConfiguration : EntityConfiguration<Location>
{
    public override void Configure(EntityTypeBuilder<Location> builder)
    {
        base.Configure(builder);
        builder.ToTable("locations");
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(250).IsRequired();
        builder.HasIndex(x => new { x.DepartmentId, x.Code }).IsUnique();
        builder.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeConfiguration : EntityConfiguration<Employee>
{
    public override void Configure(EntityTypeBuilder<Employee> builder)
    {
        base.Configure(builder);
        builder.ToTable("employees");
        builder.Property(x => x.PersonnelNumber).HasMaxLength(50).IsRequired();
        builder.Property(x => x.FullName).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => x.PersonnelNumber).IsUnique();
        builder.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserAccountConfiguration : EntityConfiguration<UserAccount>
{
    public override void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        base.Configure(builder);
        builder.ToTable("user_accounts");
        builder.Property(x => x.Login).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NormalizedLogin).HasMaxLength(100).IsRequired();
        builder.Property(x => x.PasswordHash).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(40);
        builder.HasIndex(x => x.NormalizedLogin).IsUnique();
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : EntityConfiguration<RefreshToken>
{
    public override void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        base.Configure(builder);
        builder.ToTable("refresh_tokens");
        builder.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Device).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssetConfiguration : EntityConfiguration<Asset>
{
    public override void Configure(EntityTypeBuilder<Asset> builder)
    {
        base.Configure(builder);
        builder.ToTable("assets");
        builder.Property(x => x.InventoryNumber).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(500).IsRequired();
        builder.Property(x => x.AccountingAccount).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SerialNumber).HasMaxLength(200);
        builder.Property(x => x.InitialCost).HasPrecision(19, 2);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.InventoryNumber).IsUnique();
        builder.HasIndex(x => new { x.MateriallyResponsibleEmployeeId, x.Status });
        builder.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.MateriallyResponsibleEmployee).WithMany().HasForeignKey(x => x.MateriallyResponsibleEmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AssetAssignmentHistoryConfiguration : EntityConfiguration<AssetAssignmentHistory>
{
    public override void Configure(EntityTypeBuilder<AssetAssignmentHistory> builder)
    {
        base.Configure(builder);
        builder.ToTable("asset_assignment_history");
        builder.HasIndex(x => new { x.AssetId, x.ValidFrom });
        builder.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InventoryScheduleConfiguration : EntityConfiguration<InventorySchedule>
{
    public override void Configure(EntityTypeBuilder<InventorySchedule> builder)
    {
        base.Configure(builder);
        builder.ToTable("inventory_schedules", table => table.HasCheckConstraint("ck_inventory_schedules_day_of_month", "day_of_month BETWEEN 1 AND 31"));
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LaunchMode).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ConsecutiveFailures).HasDefaultValue(0);
        builder.HasIndex(x => new { x.IsEnabled, x.NextRunAt });
    }
}

internal sealed class IntegrationEventConfiguration : EntityConfiguration<IntegrationEvent>
{
    public override void Configure(EntityTypeBuilder<IntegrationEvent> builder)
    {
        base.Configure(builder);
        builder.ToTable("integration_events");
        builder.Property(x => x.ExternalEventId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(50);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb");
        builder.HasIndex(x => x.ExternalEventId).IsUnique();
        builder.HasIndex(x => new { x.Status, x.ReceivedAt });
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }
}

internal sealed class InventoryConfiguration : EntityConfiguration<Inventory>
{
    public override void Configure(EntityTypeBuilder<Inventory> builder)
    {
        base.Configure(builder);
        builder.ToTable("inventories");
        builder.Property(x => x.Number).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Reason).HasConversion<string>().HasMaxLength(40);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        builder.Property(x => x.Round).HasDefaultValue(0);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.Status, x.PlannedAt });
        builder.HasIndex(x => new { x.SourceScheduleId, x.ScheduledPeriod }).IsUnique();
        builder.Property(x => x.ReportContextJson).HasColumnType("jsonb");
        builder.HasOne<Inventory>().WithMany().HasForeignKey(x => x.SourceInventoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InventoryAssetSnapshotConfiguration : EntityConfiguration<InventoryAssetSnapshot>
{
    public override void Configure(EntityTypeBuilder<InventoryAssetSnapshot> builder)
    {
        base.Configure(builder);
        builder.ToTable("inventory_asset_snapshots");
        builder.Property(x => x.InitialCost).HasPrecision(19, 2);
        builder.Property(x => x.Result).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Condition).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(x => new { x.InventoryId, x.AssetId }).IsUnique();
        builder.HasOne(x => x.Inventory).WithMany(x => x.Items).HasForeignKey(x => x.InventoryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InventoryAssignmentConfiguration : EntityConfiguration<InventoryAssignment>
{
    public override void Configure(EntityTypeBuilder<InventoryAssignment> builder)
    {
        base.Configure(builder);
        builder.ToTable("inventory_assignments");
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ConfirmationStatus).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(x => x.InventoryId).IsUnique();
        builder.HasOne(x => x.Inventory).WithMany(x => x.Assignments).HasForeignKey(x => x.InventoryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UnexpectedScanConfiguration : EntityConfiguration<UnexpectedScan>
{
    public override void Configure(EntityTypeBuilder<UnexpectedScan> builder)
    {
        base.Configure(builder);
        builder.ToTable("unexpected_scans");
        builder.Property(x => x.RawValue).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(x => new { x.InventoryId, x.ScannedAt });
        builder.HasOne<Inventory>().WithMany().HasForeignKey(x => x.InventoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GeneratedDocumentConfiguration : EntityConfiguration<GeneratedDocument>
{
    public override void Configure(EntityTypeBuilder<GeneratedDocument> builder)
    {
        base.Configure(builder);
        builder.ToTable("generated_documents");
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
        builder.HasIndex(x => new { x.InventoryId, x.Type }).IsUnique();
        builder.HasOne<Inventory>().WithMany().HasForeignKey(x => x.InventoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditLogConfiguration : EntityConfiguration<AuditLog>
{
    public override void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        base.Configure(builder);
        builder.ToTable("audit_logs");
        builder.Property(x => x.DataJson).HasColumnType("jsonb");
        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => new { x.EntityType, x.EntityId });
    }
}
