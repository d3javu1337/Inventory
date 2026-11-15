namespace AssetInventory.Domain;

public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
}

public sealed class Organization : Entity
{
    public required string Name { get; set; }
    public required string Inn { get; set; }
    public string? Kpp { get; set; }
    public string? Okpo { get; set; }
    public string? Address { get; set; }
    public string? DirectorName { get; set; }
}

public sealed class Department : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsInventoryDepartment { get; set; }
    public bool IsArchived { get; set; }
}

public sealed class Location : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public bool IsArchived { get; set; }
}

public sealed class Employee : Entity
{
    public required string PersonnelNumber { get; set; }
    public required string FullName { get; set; }
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public bool IsMateriallyResponsible { get; set; }
    public bool IsEmployed { get; set; } = true;
    public bool IsArchived { get; set; }
}

public sealed class UserAccount : Entity
{
    public required string Login { get; set; }
    public required string NormalizedLogin { get; set; }
    public required string PasswordHash { get; set; }
    public int SecurityVersion { get; set; }
    public UserRole Role { get; set; }
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public bool IsBlocked { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class RefreshToken : Entity
{
    public int SecurityVersion { get; set; }
    public Guid UserId { get; set; }
    public UserAccount User { get; set; } = null!;
    public required string TokenHash { get; set; }
    public required string Device { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

public sealed class Asset : Entity
{
    public required string InventoryNumber { get; set; }
    public required string Name { get; set; }
    public required string AccountingAccount { get; set; }
    public string? SerialNumber { get; set; }
    public int? ManufactureYear { get; set; }
    public DateOnly AcceptedAt { get; set; }
    public decimal InitialCost { get; set; }
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public Guid MateriallyResponsibleEmployeeId { get; set; }
    public Employee MateriallyResponsibleEmployee { get; set; } = null!;
    public AssetStatus Status { get; set; } = AssetStatus.InUse;
    public DateTimeOffset? ArchivedAt { get; set; }
    public long Version { get; set; }
}

public sealed class AssetAssignmentHistory : Entity
{
    public Guid AssetId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid LocationId { get; set; }
    public Guid MateriallyResponsibleEmployeeId { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
}

public sealed class InventorySchedule : Entity
{
    public required string Name { get; set; }
    public int IntervalMonths { get; set; } = 12;
    public DateTimeOffset NextRunAt { get; set; }
    public int? DayOfMonth { get; set; }
    public ScheduleLaunchMode LaunchMode { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
}

public sealed class IntegrationEvent : Entity
{
    public required string ExternalEventId { get; set; }
    public IntegrationEventType Type { get; set; }
    public required string PayloadJson { get; set; }
    public IntegrationEventStatus Status { get; set; } = IntegrationEventStatus.Accepted;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? ResultMessage { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public Guid? CreatedInventoryId { get; set; }
}

public sealed class Inventory : Entity
{
    public required string Number { get; set; }
    public InventoryType Type { get; set; }
    public InventoryReason Reason { get; set; }
    public string? ReasonDetails { get; set; }
    public InventoryStatus Status { get; set; } = InventoryStatus.Draft;
    public Guid? TargetEmployeeId { get; set; }
    public DateTimeOffset PlannedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public int Round { get; set; }
    public DateTimeOffset? ScanningFinishedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? SourceScheduleId { get; set; }
    public Guid? SourceInventoryId { get; set; }
    public DateOnly? ScheduledPeriod { get; set; }
    public string? ReportContextJson { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public ICollection<InventoryAssetSnapshot> Items { get; set; } = [];
    public ICollection<InventoryAssignment> Assignments { get; set; } = [];
}

public sealed class InventoryAssetSnapshot : Entity
{
    public Guid InventoryId { get; set; }
    public Inventory Inventory { get; set; } = null!;
    public Guid AssetId { get; set; }
    public required string InventoryNumber { get; set; }
    public required string Name { get; set; }
    public required string AccountingAccount { get; set; }
    public string? SerialNumber { get; set; }
    public int? ManufactureYear { get; set; }
    public DateOnly AcceptedAt { get; set; }
    public decimal InitialCost { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? MateriallyResponsibleEmployeeId { get; set; }
    public required string DepartmentName { get; set; }
    public required string LocationName { get; set; }
    public required string MateriallyResponsibleFullName { get; set; }
    public ScanResult Result { get; set; } = ScanResult.Pending;
    public AssetCondition? Condition { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset? ScannedAt { get; set; }
    public Guid? ScannedByUserId { get; set; }
}

public sealed class InventoryAssignment : Entity
{
    public Guid InventoryId { get; set; }
    public Inventory Inventory { get; set; } = null!;
    public Guid UserId { get; set; }
    public UserAccount User { get; set; } = null!;
    public CommissionRole Role { get; set; }
    public ConfirmationStatus ConfirmationStatus { get; set; } = ConfirmationStatus.Pending;
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? RejectionReason { get; set; }
}

public sealed class UnexpectedScan : Entity
{
    public Guid InventoryId { get; set; }
    public Guid ScannedByUserId { get; set; }
    public required string RawValue { get; set; }
    public UnexpectedScanType Type { get; set; }
    public Guid? RecognizedAssetId { get; set; }
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class GeneratedDocument : Entity
{
    public Guid InventoryId { get; set; }
    public DocumentType Type { get; set; }
    public required string StorageKey { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required string Sha256 { get; set; }
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLog : Entity
{
    public Guid? UserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? DataJson { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum UserRole { Administrator, InventoryEmployee }
public enum AssetStatus { InUse, InRepair, Retired, Archived }
public enum InventoryType { Planned, EventDriven, Manual }
public enum InventoryReason { AnnualReporting, MolChange, MolDismissal, AssetTransfer, LossOrDamage, Emergency, Reorganization, Liquidation, Other }
public enum InventoryStatus { Draft, NeedsAttention, Active, AwaitingConfirmation, ReviewRequired, Completed, Cancelled }
public enum ScanResult { Pending, Found, NotFound }
public enum AssetCondition { Serviceable, Damaged, Unusable }
public enum UnexpectedScanType { UnknownQr, OutOfScope }
public enum CommissionRole { Chairperson, Member }
public enum ConfirmationStatus { Pending, Confirmed, Rejected }
public enum ScheduleLaunchMode { CreateDraft, AutoStart }
public enum IntegrationEventType { EmployeeDismissed, MolChanged }
public enum IntegrationEventStatus { Accepted, Processing, Processed, Failed }
public enum DocumentType { InventoryActXlsx, InventoryActPdf }
