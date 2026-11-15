using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AssetInventory.Infrastructure.Security;

internal static class UserAccess
{
    public static Task<bool> IsActiveAsync(AppDbContext db, Guid userId, CancellationToken ct, int? securityVersion = null) => db.Users.AnyAsync(x => x.Id == userId && !x.IsBlocked
        && (securityVersion == null || x.SecurityVersion == securityVersion)
        && (x.Role != AssetInventory.Domain.UserRole.InventoryEmployee || x.Employee != null && x.Employee.Department.IsInventoryDepartment)
        && (x.EmployeeId == null || x.Employee != null && !x.Employee.IsArchived && x.Employee.IsEmployed && !x.Employee.Department.IsArchived), ct);
}
