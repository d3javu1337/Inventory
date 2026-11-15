using AssetInventory.Application.Catalog;
using AssetInventory.Contracts.Catalog;
using AssetInventory.Domain;

namespace AssetInventory.Api.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1").WithTags("Catalog")
            .RequireAuthorization(policy => policy.RequireRole(nameof(UserRole.Administrator)));

        group.MapGet("/departments/paged", (int? page, int? pageSize, string? search, bool? includeArchived, ICatalogService service, CancellationToken ct) => service.GetDepartmentsPageAsync(new(page ?? 1, pageSize ?? 20, search, includeArchived ?? false), ct));
        group.MapGet("/locations/paged", (int? page, int? pageSize, string? search, bool? includeArchived, Guid? departmentId, ICatalogService service, CancellationToken ct) => service.GetLocationsPageAsync(new(page ?? 1, pageSize ?? 20, search, includeArchived ?? false, departmentId), ct));
        group.MapGet("/employees/paged", (int? page, int? pageSize, string? search, bool? includeArchived, ICatalogService service, CancellationToken ct) => service.GetEmployeesPageAsync(new(page ?? 1, pageSize ?? 20, search, includeArchived ?? false), ct));
        group.MapGet("/users/paged", (int? page, int? pageSize, string? search, ICatalogService service, CancellationToken ct) => service.GetUsersPageAsync(new(page ?? 1, pageSize ?? 20, search), ct));
        group.MapGet("/organization", (ICatalogService service, CancellationToken ct) => service.GetOrganizationAsync(ct));
        group.MapPut("/organization", (UpsertOrganizationRequest request, ICatalogService service, CancellationToken ct) => service.UpsertOrganizationAsync(request, ct));

        group.MapGet("/departments", (bool? includeArchived, ICatalogService service, CancellationToken ct) => service.GetDepartmentsAsync(includeArchived ?? false, ct));
        group.MapGet("/departments/{id:guid}", (Guid id, ICatalogService service, CancellationToken ct) => service.GetDepartmentAsync(id, ct));
        group.MapPost("/departments", async (UpsertDepartmentRequest request, ICatalogService service, CancellationToken ct) =>
        {
            var created = await service.CreateDepartmentAsync(request, ct);
            return TypedResults.Created($"/api/v1/departments/{created.Id}", created);
        });
        group.MapPut("/departments/{id:guid}", (Guid id, UpsertDepartmentRequest request, ICatalogService service, CancellationToken ct) => service.UpdateDepartmentAsync(id, request, ct));
        group.MapDelete("/departments/{id:guid}", async (Guid id, ICatalogService service, CancellationToken ct) => { await service.ArchiveDepartmentAsync(id, ct); return TypedResults.NoContent(); });

        group.MapGet("/locations", (Guid? departmentId, bool? includeArchived, ICatalogService service, CancellationToken ct) => service.GetLocationsAsync(departmentId, includeArchived ?? false, ct));
        group.MapGet("/locations/{id:guid}", (Guid id, ICatalogService service, CancellationToken ct) => service.GetLocationAsync(id, ct));
        group.MapPost("/locations", async (UpsertLocationRequest request, ICatalogService service, CancellationToken ct) =>
        {
            var created = await service.CreateLocationAsync(request, ct);
            return TypedResults.Created($"/api/v1/locations/{created.Id}", created);
        });
        group.MapPut("/locations/{id:guid}", (Guid id, UpsertLocationRequest request, ICatalogService service, CancellationToken ct) => service.UpdateLocationAsync(id, request, ct));
        group.MapDelete("/locations/{id:guid}", async (Guid id, ICatalogService service, CancellationToken ct) => { await service.ArchiveLocationAsync(id, ct); return TypedResults.NoContent(); });

        group.MapGet("/employees", (bool? includeArchived, ICatalogService service, CancellationToken ct) => service.GetEmployeesAsync(includeArchived ?? false, ct));
        group.MapGet("/employees/{id:guid}", (Guid id, ICatalogService service, CancellationToken ct) => service.GetEmployeeAsync(id, ct));
        group.MapPost("/employees", async (UpsertEmployeeRequest request, ICatalogService service, CancellationToken ct) =>
        {
            var created = await service.CreateEmployeeAsync(request, ct);
            return TypedResults.Created($"/api/v1/employees/{created.Id}", created);
        });
        group.MapPut("/employees/{id:guid}", (Guid id, UpsertEmployeeRequest request, ICatalogService service, CancellationToken ct) => service.UpdateEmployeeAsync(id, request, ct));
        group.MapDelete("/employees/{id:guid}", async (Guid id, ICatalogService service, CancellationToken ct) => { await service.ArchiveEmployeeAsync(id, ct); return TypedResults.NoContent(); });

        group.MapGet("/users", (ICatalogService service, CancellationToken ct) => service.GetUsersAsync(ct));
        group.MapGet("/users/{id:guid}", (Guid id, ICatalogService service, CancellationToken ct) => service.GetUserAsync(id, ct));
        group.MapPost("/users", async (CreateUserRequest request, ICatalogService service, CancellationToken ct) =>
        {
            var created = await service.CreateUserAsync(request, ct);
            return TypedResults.Created($"/api/v1/users/{created.Id}", created);
        });
        group.MapPatch("/users/{id:guid}/blocked", (Guid id, SetUserBlockedRequest request, ICatalogService service, CancellationToken ct) => service.SetUserBlockedAsync(id, request.IsBlocked, ct));

        return endpoints;
    }
}
