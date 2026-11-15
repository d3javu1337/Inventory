namespace AssetInventory.Application.Common;

public interface IAuditContext
{
    Guid? UserId { get; }
    string? IpAddress { get; }
    string? CorrelationId { get; }
}
