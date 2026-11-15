using AssetInventory.Contracts.Catalog;
using AssetInventory.Contracts.Common;

namespace AssetInventory.Application.Catalog;

public interface ICatalogService
{
    Task<PagedResponse<DepartmentResponse>> GetDepartmentsPageAsync(PageQuery query, CancellationToken ct);
    Task<PagedResponse<LocationResponse>> GetLocationsPageAsync(PageQuery query, CancellationToken ct);
    Task<PagedResponse<EmployeeResponse>> GetEmployeesPageAsync(PageQuery query, CancellationToken ct);
    Task<PagedResponse<UserResponse>> GetUsersPageAsync(PageQuery query, CancellationToken ct);
    Task<OrganizationResponse?> GetOrganizationAsync(CancellationToken ct);
    Task<OrganizationResponse> UpsertOrganizationAsync(UpsertOrganizationRequest request, CancellationToken ct);
    Task<IReadOnlyList<DepartmentResponse>> GetDepartmentsAsync(bool includeArchived, CancellationToken ct);
    Task<DepartmentResponse> GetDepartmentAsync(Guid id, CancellationToken ct);
    Task<DepartmentResponse> CreateDepartmentAsync(UpsertDepartmentRequest request, CancellationToken ct);
    Task<DepartmentResponse> UpdateDepartmentAsync(Guid id, UpsertDepartmentRequest request, CancellationToken ct);
    Task ArchiveDepartmentAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<LocationResponse>> GetLocationsAsync(Guid? departmentId, bool includeArchived, CancellationToken ct);
    Task<LocationResponse> GetLocationAsync(Guid id, CancellationToken ct);
    Task<LocationResponse> CreateLocationAsync(UpsertLocationRequest request, CancellationToken ct);
    Task<LocationResponse> UpdateLocationAsync(Guid id, UpsertLocationRequest request, CancellationToken ct);
    Task ArchiveLocationAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<EmployeeResponse>> GetEmployeesAsync(bool includeArchived, CancellationToken ct);
    Task<EmployeeResponse> GetEmployeeAsync(Guid id, CancellationToken ct);
    Task<EmployeeResponse> CreateEmployeeAsync(UpsertEmployeeRequest request, CancellationToken ct);
    Task<EmployeeResponse> UpdateEmployeeAsync(Guid id, UpsertEmployeeRequest request, CancellationToken ct);
    Task ArchiveEmployeeAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<UserResponse>> GetUsersAsync(CancellationToken ct);
    Task<UserResponse> GetUserAsync(Guid id, CancellationToken ct);
    Task<UserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct);
    Task<UserResponse> SetUserBlockedAsync(Guid id, bool isBlocked, CancellationToken ct);
}
