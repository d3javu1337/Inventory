using AssetInventory.Application.Inventories;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Common;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using AssetInventory.Infrastructure.Caching;

namespace AssetInventory.Infrastructure.Inventories;

internal sealed class MonitoringService(AppDbContext db, IResponseCache cache) : IMonitoringService
{
    public Task<DashboardResponse> GetDashboardAsync(CancellationToken ct) => db.Database.CurrentTransaction is not null
        ? LoadDashboardAsync(ct) : cache.GetAsync("dashboard", "summary", () => LoadDashboardAsync(ct), ct);

    private async Task<DashboardResponse> LoadDashboardAsync(CancellationToken ct)
    {
        var activeAssets = await db.Assets.CountAsync(x => x.ArchivedAt == null && x.Status != AssetStatus.Archived, ct);
        var activeInventories = await db.Inventories.CountAsync(x => x.Status == InventoryStatus.Active || x.Status == InventoryStatus.AwaitingConfirmation || x.Status == InventoryStatus.ReviewRequired, ct);
        var pendingEvents = await db.IntegrationEvents.CountAsync(x => x.Status == IntegrationEventStatus.Accepted || x.Status == IntegrationEventStatus.Processing, ct);
        var discrepancies = await db.InventoryItems.CountAsync(x =>
            (x.Inventory.Status == InventoryStatus.Active || x.Inventory.Status == InventoryStatus.AwaitingConfirmation || x.Inventory.Status == InventoryStatus.ReviewRequired)
            && (x.Result == ScanResult.NotFound || x.Condition == AssetCondition.Damaged || x.Condition == AssetCondition.Unusable), ct);
        return new(activeAssets, activeInventories, pendingEvents, discrepancies);
    }

    public Task<PagedResponse<AuditLogResponse>> GetAuditAsync(int page, int pageSize, CancellationToken ct) => GetAuditAsync(new AuditListQuery(page, pageSize), ct);

    public async Task<PagedResponse<AuditLogResponse>> GetAuditAsync(AuditListQuery query, CancellationToken ct)
    {
        var paging = new PageQuery(query.Page, query.PageSize);
        QueryPaging.Validate(paging);
        var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        if (search.Length > 200 || query.EntityId?.Length > 200 || query.Action?.Length > 100 || query.EntityType?.Length > 100)
            throw new RequestValidationException("Превышена длина фильтра аудита.");
        if (query.DateFrom > query.DateTo) throw new RequestValidationException("Начальная дата не может быть позже конечной.");
        var source = db.AuditLogs.AsNoTracking();
        if (search.Length > 0)
        {
            // ->> decodes the historical JSON display name, including escaped
            // Cyrillic. strpos treats % and _ literally; input stays parameterized.
            source = db.AuditLogs.FromSqlInterpolated($"""
                SELECT a.* FROM audit_logs a
                LEFT JOIN user_accounts u ON u.id = a.user_id
                LEFT JOIN employees e ON e.id = u.employee_id
                LEFT JOIN inventories i ON a.entity_type = 'Inventory' AND a.entity_id = i.id::text
                WHERE strpos(lower(concat_ws(' ', a.action, a.entity_type, a.entity_id,
                    a.correlation_id, a.ip_address, a.data_json ->> 'displayName',
                    u.login, e.full_name, i.number)), {search}) > 0
                """).AsNoTracking();
        }
        if (!string.IsNullOrWhiteSpace(query.Action)) source = source.Where(x => x.Action == query.Action.Trim());
        if (!string.IsNullOrWhiteSpace(query.EntityType)) source = source.Where(x => x.EntityType == query.EntityType.Trim());
        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            var entityId = Guid.TryParse(query.EntityId, out var parsedId) ? parsedId.ToString("D") : query.EntityId.Trim();
            source = source.Where(x => x.EntityId == entityId);
        }
        if (query.UserId == Guid.Empty) source = source.Where(x => x.UserId == null || x.UserId == Guid.Empty);
        else if (query.UserId is not null) source = source.Where(x => x.UserId == query.UserId);
        var offset = TimeSpan.FromHours(5);
        if (query.DateFrom is { } dateFrom)
        {
            var from = dateFrom == DateOnly.MinValue ? DateTimeOffset.MinValue : new DateTimeOffset(dateFrom.ToDateTime(TimeOnly.MinValue), offset).ToUniversalTime();
            source = source.Where(x => x.CreatedAt >= from);
        }
        if (query.DateTo is { } dateTo)
        {
            var through = new DateTimeOffset(dateTo.ToDateTime(TimeOnly.MaxValue), offset).ToUniversalTime();
            source = source.Where(x => x.CreatedAt <= through);
        }
        var auditPage = await source.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).PageAsync(paging, ct);
        var logs = auditPage.Items;
        var userIds = logs.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id))
            .Select(x => new { x.Id, DisplayName = x.Employee != null ? x.Employee.FullName : x.Login })
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var inventoryIds = logs.Where(x => x.EntityType == nameof(Inventory) && Guid.TryParse(x.EntityId, out _))
            .Select(x => Guid.Parse(x.EntityId!)).Distinct().ToArray();
        var inventories = await db.Inventories.AsNoTracking().Where(x => inventoryIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        var items = logs.Select(x =>
        {
            var entityDisplayName = x.EntityType == nameof(Inventory) && Guid.TryParse(x.EntityId, out var inventoryId)
                ? inventories.GetValueOrDefault(inventoryId)
                : ReadDisplayName(x.DataJson);
            return new AuditLogResponse(x.Id, x.UserId, x.UserId.HasValue ? users.GetValueOrDefault(x.UserId.Value) : null, x.Action, x.EntityType, x.EntityId, entityDisplayName, x.DataJson, x.CreatedAt, x.IpAddress, x.CorrelationId);
        }).ToList();
        return new(items, auditPage.Page, auditPage.PageSize, auditPage.TotalCount);
    }

    private static string? ReadDisplayName(string? json)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
