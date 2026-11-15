namespace AssetInventory.Contracts.Common;
public sealed record PageQuery(int Page = 1, int PageSize = 20, string? Search = null, bool IncludeArchived = false, Guid? DepartmentId = null);
