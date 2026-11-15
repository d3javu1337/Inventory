using System.Text.Json.Serialization;

using System.Globalization;

namespace AssetInventory.Contracts.Inventories;

public sealed record CreateInventoryRequest(string Type, string Reason, string? ReasonDetails, Guid? TargetEmployeeId, DateTimeOffset PlannedAt);
public sealed record CloneInventoryRequest(DateTimeOffset PlannedAt);
public sealed record InventoryListQuery(string? Status, string? Type, int Page = 1, int PageSize = 50, string? Search = null, string? Reason = null);
public sealed record InventorySummaryResponse(Guid Id, string Number, string Type, string Reason, string Status, DateTimeOffset PlannedAt, int Total, int Found, int NotFound, int Pending, string? AssigneeName = null, string? TargetEmployeeName = null);
public sealed record InventoryItemResponse(Guid Id, Guid AssetId, string InventoryNumber, string Name, string AccountingAccount, decimal InitialCost, string DepartmentName, string LocationName, string MateriallyResponsibleFullName, string Result, string? Condition, string? Comment, DateTimeOffset? ScannedAt)
{
    public string InitialCostExact => InitialCost.ToString("F2", CultureInfo.InvariantCulture);
}
public sealed record InventoryAssigneeResponse(Guid Id, Guid UserId, string Login, string? EmployeeName, string Role, string ConfirmationStatus, DateTimeOffset? ConfirmedAt, string? RejectionReason);
public sealed record UnexpectedScanResponse(Guid Id, string RawValue, string Type, Guid? RecognizedAssetId, string? RecognizedInventoryNumber, string? RecognizedAssetName, DateTimeOffset ScannedAt);
public sealed record InventoryCounts(int Total, int Found, int Pending, int NotFound, int Damaged, int Discrepancies, int Unexpected);
public sealed record InventoryDetailQuery(int Page = 1, int PageSize = 20, string? Filter = null, string? Search = null);
public sealed record InventoryDetailResponse(Guid Id, string Number, string Type, string Reason, string? ReasonDetails, string Status, Guid? TargetEmployeeId, DateTimeOffset PlannedAt, DateTimeOffset? StartedAt, DateTimeOffset? ScanningFinishedAt, DateTimeOffset? CompletedAt, IReadOnlyList<InventoryItemResponse> Items, [property: JsonPropertyName("commission")] IReadOnlyList<InventoryAssigneeResponse> Assignments, IReadOnlyList<UnexpectedScanResponse> UnexpectedScans, Guid? SourceInventoryId = null, string? SourceInventoryNumber = null, InventoryCounts? Counts = null, int? Page = null, int? PageSize = null, int? ItemsTotalCount = null, int? UnexpectedTotalCount = null, int Round = 0)
{
    public InventoryAssigneeResponse? Assignee => Assignments.SingleOrDefault();
}
public sealed record ReplaceAssigneeRequest(Guid OldUserId, Guid NewUserId);
public sealed record ScanAssetRequest(string QrValue, string Condition, string? Comment);
public sealed record ScanAssetResponse(string Result, bool Duplicate, InventoryItemResponse? Asset, string Message);
public sealed record ConfirmationRequest(string Password);
public sealed record RejectionRequest(string Password, string Reason);

public sealed record UpsertScheduleRequest(string Name, int IntervalMonths, DateTimeOffset NextRunAt, string LaunchMode, bool IsEnabled);
public sealed record ScheduleResponse(Guid Id, string Name, int IntervalMonths, DateTimeOffset NextRunAt, string LaunchMode, bool IsEnabled, DateTimeOffset? LastRunAt, int ConsecutiveFailures, DateTimeOffset? RetryAt, int? DayOfMonth = null);

public sealed record MolChangeEventRequest(string EventId, Guid EmployeeId, DateTimeOffset OccurredAt, string? Details);
public sealed record DismissalEventRequest(string EventId, Guid EmployeeId, DateTimeOffset OccurredAt);
public sealed record IntegrationEventResponse(Guid Id, string EventId, string Type, string Status, int Attempts, string? LastError, Guid? CreatedInventoryId, string? CreatedInventoryNumber, DateTimeOffset ReceivedAt, DateTimeOffset? NextAttemptAt = null, string? ResultMessage = null);

public sealed record DashboardResponse(int ActiveAssets, int ActiveInventories, int PendingEvents, int UnresolvedDiscrepancies);
public sealed record AuditLogResponse(Guid Id, Guid? UserId, string? UserDisplayName, string Action, string EntityType, string? EntityId, string? EntityDisplayName, string? DataJson, DateTimeOffset CreatedAt, string? IpAddress = null, string? CorrelationId = null);
public sealed record AuditListQuery(int Page = 1, int PageSize = 50, string? Search = null, string? Action = null, string? EntityType = null, string? EntityId = null, Guid? UserId = null, DateOnly? DateFrom = null, DateOnly? DateTo = null);
public sealed record GeneratedFile(byte[] Content, string ContentType, string FileName);

public sealed record ImportPreviewRow(int RowNumber, string InventoryNumber, bool IsValid, string? Error);
public sealed record ImportPreviewResponse(Guid ImportId, int TotalRows, int ValidRows, int InvalidRows, int AppliedRows, IReadOnlyList<ImportPreviewRow> Rows);
