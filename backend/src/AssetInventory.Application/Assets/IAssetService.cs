using AssetInventory.Contracts.Assets;
using AssetInventory.Contracts.Common;

namespace AssetInventory.Application.Assets;

public interface IAssetService
{
    Task<PagedResponse<AssetResponse>> GetAsync(AssetListQuery query, CancellationToken ct);
    Task<AssetResponse> GetByIdAsync(Guid id, CancellationToken ct);
    Task<AssetResponse> CreateAsync(UpsertAssetRequest request, CancellationToken ct);
    Task<AssetResponse> UpdateAsync(Guid id, long expectedVersion, UpsertAssetRequest request, CancellationToken ct);
    Task ArchiveAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AssetAssignmentHistoryResponse>> GetHistoryAsync(Guid id, CancellationToken ct);
    Task<PagedResponse<AssetAssignmentHistoryResponse>> GetHistoryPageAsync(Guid id, PageQuery query, CancellationToken ct);
}

public interface IAssetImportService
{
    Task<AssetInventory.Contracts.Inventories.ImportPreviewResponse> ImportAsync(Stream stream, string fileName, bool apply, CancellationToken ct);
    byte[] GetCsvTemplate();
}
