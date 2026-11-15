using System.Globalization;
using System.Text.Json.Serialization;

namespace AssetInventory.Contracts.Assets;

public sealed record AssetResponse(
    Guid Id, string InventoryNumber, string Name, string AccountingAccount, string? SerialNumber,
    int? ManufactureYear, DateOnly AcceptedAt, decimal InitialCost, Guid DepartmentId,
    string DepartmentName, Guid LocationId, string LocationName, Guid MateriallyResponsibleEmployeeId,
    string MateriallyResponsibleEmployeeName, string Status, DateTimeOffset? ArchivedAt, long Version)
{
    public string InitialCostExact => InitialCost.ToString("F2", CultureInfo.InvariantCulture);
}

public sealed record UpsertAssetRequest(
    string InventoryNumber, string Name, string AccountingAccount, string? SerialNumber,
    int? ManufactureYear, DateOnly AcceptedAt, [property: JsonRequired] decimal InitialCost, Guid DepartmentId,
    Guid LocationId, Guid MateriallyResponsibleEmployeeId);

public sealed record AssetListQuery(string? Search, Guid? DepartmentId, Guid? MateriallyResponsibleEmployeeId, bool IncludeArchived, int Page = 1, int PageSize = 50);
public sealed record AssetAssignmentHistoryResponse(
    Guid Id,
    Guid DepartmentId,
    string DepartmentName,
    Guid LocationId,
    string LocationName,
    Guid MateriallyResponsibleEmployeeId,
    string MateriallyResponsibleEmployeeName,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);
