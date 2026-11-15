using AssetInventory.Application.Assets;
using AssetInventory.Api.Configuration;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Assets;
using AssetInventory.Domain;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace AssetInventory.Api.Endpoints;

public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/assets").WithTags("Assets")
            .RequireAuthorization(policy => policy.RequireRole(nameof(UserRole.Administrator)));

        group.MapGet("/", (string? search, Guid? departmentId, Guid? materiallyResponsibleEmployeeId, bool? includeArchived, int? page, int? pageSize, IAssetService service, CancellationToken ct) =>
            service.GetAsync(new AssetListQuery(search, departmentId, materiallyResponsibleEmployeeId, includeArchived ?? false, page ?? 1, pageSize ?? 50), ct));
        group.MapGet("/{id:guid}", async (Guid id, HttpResponse response, IAssetService service, CancellationToken ct) =>
        {
            var asset = await service.GetByIdAsync(id, ct);
            response.Headers.ETag = VersionTag(asset.Version);
            return asset;
        }).DescribeAssetVersion();
        group.MapGet("/{id:guid}/history/paged", (Guid id, int? page, int? pageSize, string? search, IAssetService service, CancellationToken ct) => service.GetHistoryPageAsync(id, new(page ?? 1, pageSize ?? 20, search), ct));
        group.MapGet("/{id:guid}/history", (Guid id, IAssetService service, CancellationToken ct) => service.GetHistoryAsync(id, ct));
        group.MapGet("/{id:guid}/qr", async (Guid id, AssetInventory.Application.Inventories.IDocumentService service, CancellationToken ct) => { var file = await service.GetQrAsync(id, ct); return TypedResults.File(file.Content, file.ContentType, file.FileName); }).ProducesFile("image/png");
        group.MapGet("/imports/template.csv", (IAssetImportService service) => TypedResults.File(service.GetCsvTemplate(), "text/csv; charset=utf-8", "assets-import-template.csv")).ProducesFile("text/csv");
        group.MapPost("/imports", async (IFormFile file, bool? apply, IAssetImportService service, CancellationToken ct) =>
        {
            if (file.Length == 0 || file.Length > 20 * 1024 * 1024) throw new AssetInventory.Application.Common.RequestValidationException("Файл пуст или превышает 20 МБ.");
            await using var stream = file.OpenReadStream(); return await service.ImportAsync(stream, file.FileName, apply ?? false, ct);
        }).DisableAntiforgery();
        group.MapPost("/", async (UpsertAssetRequest request, IAssetService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct); return TypedResults.Created($"/api/v1/assets/{created.Id}", created);
        });
        group.MapPut("/{id:guid}", async (Guid id, UpsertAssetRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response, IAssetService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(ifMatch))
                throw new PreconditionRequiredException("Заголовок If-Match обязателен.");
            if (!EntityTagHeaderValue.TryParseStrictList([ifMatch], out var tags)
                || (tags.Count > 1 && tags.Contains(EntityTagHeaderValue.Any)))
                throw new RequestValidationException("Некорректный заголовок If-Match.");

            var current = await service.GetByIdAsync(id, ct);
            var currentTag = EntityTagHeaderValue.Parse(VersionTag(current.Version));
            if (!tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(currentTag, useStrongComparison: true)))
                throw new PreconditionFailedException("Карточка была изменена другим пользователем.");

            var updated = await service.UpdateAsync(id, current.Version, request, ct);
            response.Headers.ETag = VersionTag(updated.Version);
            return updated;
        }).DescribeAssetVersion(requiresIfMatch: true)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        group.MapDelete("/{id:guid}", async (Guid id, IAssetService service, CancellationToken ct) => { await service.ArchiveAsync(id, ct); return TypedResults.NoContent(); });
        return endpoints;
    }

    private static string VersionTag(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
}
