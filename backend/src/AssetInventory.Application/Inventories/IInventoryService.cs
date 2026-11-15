using AssetInventory.Contracts.Common;
using AssetInventory.Contracts.Inventories;

namespace AssetInventory.Application.Inventories;

public interface IInventoryService
{
    Task<PagedResponse<InventorySummaryResponse>> GetAsync(InventoryListQuery query, Guid? assignedUserId, CancellationToken ct);
    Task<InventoryDetailResponse> GetByIdAsync(Guid id, Guid? assignedUserId, CancellationToken ct);
    Task<InventoryDetailResponse> GetPageAsync(Guid id, InventoryDetailQuery query, Guid? assignedUserId, CancellationToken ct);
    Task<InventoryDetailResponse> CreateAsync(CreateInventoryRequest request, Guid actorId, CancellationToken ct);
    Task<InventoryDetailResponse> CloneAsync(Guid id, CloneInventoryRequest request, Guid actorId, CancellationToken ct);
    Task<InventoryDetailResponse> StartAsync(Guid id, Guid actorId, CancellationToken ct);
    Task<InventoryDetailResponse> CancelAsync(Guid id, Guid actorId, CancellationToken ct);
    Task<InventoryDetailResponse> ReplaceAssigneeAsync(Guid id, ReplaceAssigneeRequest request, Guid actorId, CancellationToken ct);
    Task<ScanAssetResponse> ScanAsync(Guid id, ScanAssetRequest request, Guid actorId, CancellationToken ct, int? expectedRound = null);
    Task<InventoryDetailResponse> FinishScanningAsync(Guid id, Guid actorId, CancellationToken ct);
    Task<InventoryDetailResponse?> FinishScanningAsync(Guid id, Guid actorId, bool includeDetails, CancellationToken ct, int? expectedRound = null);
    Task<InventoryDetailResponse> ConfirmAsync(Guid id, ConfirmationRequest request, Guid actorId, CancellationToken ct);
    Task<InventoryDetailResponse?> ConfirmAsync(Guid id, ConfirmationRequest request, Guid actorId, bool includeDetails, CancellationToken ct, int? expectedRound = null);
    Task<InventoryDetailResponse> RejectAsync(Guid id, RejectionRequest request, Guid actorId, CancellationToken ct);
    Task<InventoryDetailResponse?> RejectAsync(Guid id, RejectionRequest request, Guid actorId, bool includeDetails, CancellationToken ct, int? expectedRound = null);
}

public interface IScheduleService
{
    Task<PagedResponse<ScheduleResponse>> GetPageAsync(PageQuery query, CancellationToken ct);
    Task<IReadOnlyList<ScheduleResponse>> GetAsync(CancellationToken ct);
    Task<ScheduleResponse> GetByIdAsync(Guid id, CancellationToken ct);
    Task<ScheduleResponse> CreateAsync(UpsertScheduleRequest request, CancellationToken ct);
    Task<ScheduleResponse> UpdateAsync(Guid id, UpsertScheduleRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface IIntegrationEventService
{
    Task<PagedResponse<IntegrationEventResponse>> GetPageAsync(PageQuery query, CancellationToken ct);
    Task<IntegrationEventResponse> RegisterMolChangeAsync(MolChangeEventRequest request, CancellationToken ct);
    Task<IntegrationEventResponse> RegisterDismissalAsync(DismissalEventRequest request, CancellationToken ct);
    Task<IntegrationEventResponse> RetryAsync(Guid id, Guid actorId, CancellationToken ct);
    Task<IReadOnlyList<IntegrationEventResponse>> GetAsync(CancellationToken ct);
}

public interface IInventoryJobProcessor
{
    Task ProcessEventsAsync(CancellationToken ct);
    Task ProcessSchedulesAsync(CancellationToken ct);
}

public interface IDocumentService
{
    Task GenerateInventoryReportsAsync(Guid inventoryId, CancellationToken ct);
    Task<GeneratedFile> GetQrAsync(Guid assetId, CancellationToken ct);
    Task<GeneratedFile> GetInventoryReportAsync(Guid inventoryId, string format, Guid? assignedUserId, CancellationToken ct);
}

public interface IMonitoringService
{
    Task<DashboardResponse> GetDashboardAsync(CancellationToken ct);
    Task<PagedResponse<AuditLogResponse>> GetAuditAsync(int page, int pageSize, CancellationToken ct);
    Task<PagedResponse<AuditLogResponse>> GetAuditAsync(AuditListQuery query, CancellationToken ct);
}
