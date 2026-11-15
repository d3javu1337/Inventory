using System.Text.Json;
using AssetInventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AssetInventory.Infrastructure.Persistence;

internal static class ChangeAudit
{
    // Explicit allowlists: never serialize entire entities, passwords, token hashes or import payloads.
    private static readonly Dictionary<Type, (string Prefix, string[] Fields)> Rules = new()
    {
        [typeof(Organization)] = ("organization", ["Name", "Inn", "Kpp", "Okpo", "Address", "DirectorName"]),
        [typeof(Department)] = ("department", ["Code", "Name", "IsInventoryDepartment", "IsArchived"]),
        [typeof(Location)] = ("location", ["Code", "Name", "DepartmentId", "IsArchived"]),
        [typeof(Employee)] = ("employee", ["PersonnelNumber", "FullName", "DepartmentId", "IsMateriallyResponsible", "IsEmployed", "IsArchived"]),
        [typeof(UserAccount)] = ("user", ["Login", "Role", "EmployeeId", "IsBlocked"]),
        [typeof(Asset)] = ("asset", ["InventoryNumber", "Name", "DepartmentId", "LocationId", "MateriallyResponsibleEmployeeId", "Status", "ArchivedAt"]),
        [typeof(InventorySchedule)] = ("schedule", ["Name", "IntervalMonths", "NextRunAt", "LaunchMode", "IsEnabled", "LastRunAt", "ConsecutiveFailures", "RetryAt"]),
        [typeof(IntegrationEvent)] = ("event", ["ExternalEventId", "Type", "Status", "Attempts", "NextAttemptAt", "CreatedInventoryId"]),
    };

    public static AuditLog? Create(EntityEntry entry)
    {
        if (entry.Entity is not Entity entity || !Rules.TryGetValue(entry.Entity.GetType(), out var rule)) return null;
        if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) return null;
        var fields = entry.Properties.Where(p => rule.Fields.Contains(p.Metadata.Name)
            && (entry.State != EntityState.Modified || p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))).ToArray();
        // Record that an asset changed even when only a non-logged field (e.g. cost) changed.
        if (fields.Length == 0 && entry.Entity is not Asset) return null;
        var operation = entry.State switch { EntityState.Added => "created", EntityState.Deleted => "deleted", _ => "updated" };
        if (entry.State == EntityState.Modified && fields.Any(p => p.Metadata.Name == "IsArchived" && Equals(p.CurrentValue, true)
            || p.Metadata.Name == "ArchivedAt" && p.CurrentValue is not null)) operation = "archived";
        if (entry.Entity is UserAccount && fields.Any(p => p.Metadata.Name == "IsBlocked") && entry.State == EntityState.Modified)
            operation = ((UserAccount)entry.Entity).IsBlocked ? "blocked" : "unblocked";
        var changes = fields.ToDictionary(p => p.Metadata.Name, p => new
        {
            before = entry.State == EntityState.Added ? null : p.OriginalValue,
            after = entry.State == EntityState.Deleted ? null : p.CurrentValue,
        });
        var displayName = entry.Entity switch
        {
            Organization x => x.Name, Department x => $"{x.Code} — {x.Name}", Location x => $"{x.Code} — {x.Name}",
            Employee x => x.FullName, UserAccount x => x.Login, Asset x => $"{x.InventoryNumber} — {x.Name}",
            InventorySchedule x => x.Name, IntegrationEvent x => x.ExternalEventId, _ => null,
        };
        return new AuditLog { Action = $"{rule.Prefix}.{operation}", EntityType = entry.Metadata.ClrType.Name,
            EntityId = entity.Id.ToString(), DataJson = JsonSerializer.Serialize(new { displayName, changes }) };
    }
}
