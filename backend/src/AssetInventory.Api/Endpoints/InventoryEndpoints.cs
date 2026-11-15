using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AssetInventory.Api.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/v1").RequireAuthorization(p => p.RequireRole(nameof(UserRole.Administrator)));
        admin.MapGet("/inventories", (string? status, string? type, int? page, int? pageSize, string? search, string? reason, IInventoryService service, CancellationToken ct) => service.GetAsync(new(status, type, page ?? 1, pageSize ?? 50, search, reason), null, ct));
        admin.MapGet("/inventories/{id:guid}", (Guid id, int? page, int? pageSize, string? filter, string? search, IInventoryService service, CancellationToken ct) => page.HasValue ? service.GetPageAsync(id, new(page.Value, pageSize ?? 20, filter, search), null, ct) : service.GetByIdAsync(id, null, ct));
        admin.MapPost("/inventories", async (CreateInventoryRequest request, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, UserId(user), ct);
            return TypedResults.Created($"/api/v1/inventories/{created.Id}", created);
        });
        admin.MapPost("/inventories/{id:guid}/clone", async (Guid id, CloneInventoryRequest request, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) =>
        {
            var clone = await service.CloneAsync(id, request, UserId(user), ct);
            return TypedResults.Created($"/api/v1/inventories/{clone.Id}", clone);
        });
        admin.MapPost("/inventories/{id:guid}/start", (Guid id, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) => service.StartAsync(id, UserId(user), ct));
        admin.MapPost("/inventories/{id:guid}/cancel", (Guid id, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) => service.CancelAsync(id, UserId(user), ct));
        admin.MapPut("/inventories/{id:guid}/assignee", ReplaceAssigneeAsync);
        admin.MapPut("/inventories/{id:guid}/commission", ReplaceAssigneeAsync).ExcludeFromDescription();
        admin.MapGet("/inventory-schedules/paged", (int? page, int? pageSize, string? search, IScheduleService service, CancellationToken ct) => service.GetPageAsync(new(page ?? 1, pageSize ?? 20, search), ct));
        admin.MapGet("/inventory-events/paged", (int? page, int? pageSize, string? search, IIntegrationEventService service, CancellationToken ct) => service.GetPageAsync(new(page ?? 1, pageSize ?? 20, search), ct));
        admin.MapGet("/inventory-schedules", (IScheduleService service, CancellationToken ct) => service.GetAsync(ct));
        admin.MapGet("/inventory-schedules/{id:guid}", (Guid id, IScheduleService service, CancellationToken ct) => service.GetByIdAsync(id, ct));
        admin.MapPost("/inventory-schedules", async (UpsertScheduleRequest request, IScheduleService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return TypedResults.Created($"/api/v1/inventory-schedules/{created.Id}", created);
        });
        admin.MapPut("/inventory-schedules/{id:guid}", (Guid id, UpsertScheduleRequest request, IScheduleService service, CancellationToken ct) => service.UpdateAsync(id, request, ct));
        admin.MapDelete("/inventory-schedules/{id:guid}", async (Guid id, IScheduleService service, CancellationToken ct) => { await service.DeleteAsync(id, ct); return TypedResults.NoContent(); });
        admin.MapPost("/inventory-events/mol-change", async (MolChangeEventRequest request, IIntegrationEventService service, CancellationToken ct) => TypedResults.Accepted(uri: (string?)null, value: await service.RegisterMolChangeAsync(request, ct)));
        admin.MapGet("/inventory-events", (IIntegrationEventService service, CancellationToken ct) => service.GetAsync(ct));
        admin.MapPost("/inventory-events/{id:guid}/retry", async (Guid id, ClaimsPrincipal user, IIntegrationEventService service, CancellationToken ct) => TypedResults.Accepted(uri: (string?)null, value: await service.RetryAsync(id, UserId(user), ct)));
        admin.MapGet("/dashboard", (IMonitoringService service, CancellationToken ct) => service.GetDashboardAsync(ct));
        admin.MapGet("/audit", (int? page, int? pageSize, string? search, string? action, string? entityType, string? entityId, Guid? userId, DateOnly? dateFrom, DateOnly? dateTo, IMonitoringService service, CancellationToken ct) => service.GetAuditAsync(new AuditListQuery(page ?? 1, pageSize ?? 50, search, action, entityType, entityId, userId, dateFrom, dateTo), ct));

        endpoints.MapPost("/internal/v1/hr-events/dismissals", async (DismissalEventRequest request, IIntegrationEventService service, CancellationToken ct) => TypedResults.Accepted(uri: (string?)null, value: await service.RegisterDismissalAsync(request, ct))).AllowAnonymous();

        var authenticated = endpoints.MapGroup("/api/v1").RequireAuthorization();
        authenticated.MapGet("/inventories/{id:guid}/reports/inv-1.{format}", async (Guid id, string format, ClaimsPrincipal user, IDocumentService service, CancellationToken ct) => { var file = await service.GetInventoryReportAsync(id, format, user.IsInRole(nameof(UserRole.Administrator)) ? null : UserId(user), ct); return TypedResults.File(file.Content, file.ContentType, file.FileName); })
            .ProducesFile("application/pdf", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        var mobile = endpoints.MapGroup("/api/v1/mobile").RequireAuthorization(p => p.RequireRole(nameof(UserRole.InventoryEmployee)));
        mobile.MapGet("/inventories", (string? status, int? page, int? pageSize, string? search, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) => service.GetAsync(new(status, null, page ?? 1, pageSize ?? 50, search), UserId(user), ct));
        mobile.MapGet("/inventories/{id:guid}", (Guid id, int? page, int? pageSize, string? filter, string? search, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) => page.HasValue ? service.GetPageAsync(id, new(page.Value, pageSize ?? 20, filter, search), UserId(user), ct) : service.GetByIdAsync(id, UserId(user), ct));
        mobile.MapPost("/inventories/{id:guid}/scan", (Guid id, int? expectedRound, ScanAssetRequest request, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) => service.ScanAsync(id, request, UserId(user), ct, expectedRound));
        mobile.MapPost("/inventories/{id:guid}/finish-scanning", async (Guid id, bool? includeDetails, int? expectedRound, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) =>
            WithOptionalDetails(await service.FinishScanningAsync(id, UserId(user), includeDetails != false, ct, expectedRound)));
        mobile.MapPost("/inventories/{id:guid}/confirm", async (Guid id, bool? includeDetails, int? expectedRound, ConfirmationRequest request, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) =>
            WithOptionalDetails(await service.ConfirmAsync(id, request, UserId(user), includeDetails != false, ct, expectedRound)));
        mobile.MapPost("/inventories/{id:guid}/reject", async (Guid id, bool? includeDetails, int? expectedRound, RejectionRequest request, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) =>
            WithOptionalDetails(await service.RejectAsync(id, request, UserId(user), includeDetails != false, ct, expectedRound)));
        return endpoints;
    }

    private static Task<InventoryDetailResponse> ReplaceAssigneeAsync(Guid id, ReplaceAssigneeRequest request, ClaimsPrincipal user, IInventoryService service, CancellationToken ct) =>
        service.ReplaceAssigneeAsync(id, request, UserId(user), ct);

    private static Results<Ok<InventoryDetailResponse>, NoContent> WithOptionalDetails(InventoryDetailResponse? detail) =>
        detail is null ? TypedResults.NoContent() : TypedResults.Ok(detail);

    internal static Guid UserId(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(value, out var id) ? id : throw new UnauthorizedException();
    }
}
