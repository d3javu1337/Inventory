namespace AssetInventory.Contracts.Catalog;

public sealed record OrganizationResponse(Guid Id, string Name, string Inn, string? Kpp, string? Okpo, string? Address, string? DirectorName);
public sealed record UpsertOrganizationRequest(string Name, string Inn, string? Kpp, string? Okpo, string? Address, string? DirectorName);

public sealed record DepartmentResponse(Guid Id, string Code, string Name, bool IsInventoryDepartment, bool IsArchived);
public sealed record UpsertDepartmentRequest(string Code, string Name, bool IsInventoryDepartment);

public sealed record LocationResponse(Guid Id, string Code, string Name, Guid DepartmentId, string DepartmentName, bool IsArchived);
public sealed record UpsertLocationRequest(string Code, string Name, Guid DepartmentId);

public sealed record EmployeeResponse(Guid Id, string PersonnelNumber, string FullName, Guid DepartmentId, string DepartmentName, bool IsMateriallyResponsible, bool IsEmployed, bool IsArchived);
public sealed record UpsertEmployeeRequest(string PersonnelNumber, string FullName, Guid DepartmentId, bool IsMateriallyResponsible, bool IsEmployed);

public sealed record UserResponse(Guid Id, string Login, string Role, Guid? EmployeeId, string? EmployeeName, bool IsBlocked, DateTimeOffset CreatedAt);
public sealed record CreateUserRequest(string Login, string Password, string Role, Guid? EmployeeId);
public sealed record SetUserBlockedRequest(bool IsBlocked);
