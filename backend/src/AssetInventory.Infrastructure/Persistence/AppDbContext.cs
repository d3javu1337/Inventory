using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql.NameTranslation;
using AssetInventory.Domain;
using AssetInventory.Application.Common;
using AssetInventory.Infrastructure.Caching;

namespace AssetInventory.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IAuditContext? auditContext = null, IResponseCache? cache = null) : DbContext(options)
{
    private readonly HashSet<string> cacheGroups = [];

    internal void ClearCacheInvalidations() => cacheGroups.Clear();
    internal async Task PublishCacheInvalidationsAsync()
    {
        var groups = cacheGroups.ToArray(); cacheGroups.Clear();
        if (cache is not null && groups.Length > 0) await cache.InvalidateAsync(groups);
    }

    private void PrepareCacheInvalidations()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is Organization or Department or Location or Employee or UserAccount) cacheGroups.Add("catalog");
            if (entry.Entity is Asset or Inventory or InventoryAssetSnapshot or IntegrationEvent) cacheGroups.Add("dashboard");
        }
    }
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetAssignmentHistory> AssetAssignmentHistory => Set<AssetAssignmentHistory>();
    public DbSet<InventorySchedule> InventorySchedules => Set<InventorySchedule>();
    public DbSet<IntegrationEvent> IntegrationEvents => Set<IntegrationEvent>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<InventoryAssetSnapshot> InventoryItems => Set<InventoryAssetSnapshot>();
    public DbSet<InventoryAssignment> InventoryAssignments => Set<InventoryAssignment>();
    public DbSet<UnexpectedScan> UnexpectedScans => Set<UnexpectedScan>();
    public DbSet<GeneratedDocument> GeneratedDocuments => Set<GeneratedDocument>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var generated = PrepareAudit();
        PrepareCacheInvalidations();
        try
        {
            var count = base.SaveChanges(acceptAllChangesOnSuccess);
            if (Database.CurrentTransaction is null) PublishCacheInvalidationsAsync().GetAwaiter().GetResult();
            return count;
        }
        catch { foreach (var log in generated) Entry(log).State = EntityState.Detached; throw; }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var generated = PrepareAudit();
        PrepareCacheInvalidations();
        try
        {
            var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (Database.CurrentTransaction is null) await PublishCacheInvalidationsAsync();
            return count;
        }
        catch { foreach (var log in generated) Entry(log).State = EntityState.Detached; throw; }
    }

    private List<AuditLog> PrepareAudit()
    {
        ChangeTracker.DetectChanges();
        var entries = ChangeTracker.Entries().ToArray();
        if (entries.Any(e => e.Entity is AuditLog && e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Записи аудита нельзя изменять или удалять.");
        var generated = entries.Select(ChangeAudit.Create).OfType<AuditLog>().ToList();
        AuditLogs.AddRange(generated);
        foreach (var entry in ChangeTracker.Entries<AuditLog>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.UserId ??= auditContext?.UserId;
            entry.Entity.IpAddress ??= auditContext?.IpAddress;
            entry.Entity.CorrelationId ??= auditContext?.CorrelationId;
        }
        return generated;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);

        var names = new NpgsqlSnakeCaseNameTranslator();
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            foreach (var property in entity.GetProperties())
                property.SetColumnName(names.TranslateMemberName(property.GetColumnName(table)!));
        }

        // Default EF names are based on mapped columns. Normalize their prefixes too.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var key in entity.GetKeys())
                key.SetName(names.TranslateMemberName(key.GetName()!));
            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName(names.TranslateMemberName(index.GetDatabaseName()!));
            foreach (var foreignKey in entity.GetForeignKeys())
                foreignKey.SetConstraintName(names.TranslateMemberName(foreignKey.GetConstraintName()!));
        }
    }
}
