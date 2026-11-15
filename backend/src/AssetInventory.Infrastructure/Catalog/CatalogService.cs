using AssetInventory.Application.Catalog;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Catalog;
using AssetInventory.Contracts.Common;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AssetInventory.Infrastructure.Caching;
using Microsoft.EntityFrameworkCore.Storage;

namespace AssetInventory.Infrastructure.Catalog;

internal sealed class CatalogService(AppDbContext db, IPasswordHasher<UserAccount> passwordHasher, IResponseCache cache) : ICatalogService
{
    public Task<PagedResponse<DepartmentResponse>> GetDepartmentsPageAsync(PageQuery query, CancellationToken ct)
    {
        QueryPaging.Validate(query); var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        return db.Departments.AsNoTracking().Where(x => (query.IncludeArchived || !x.IsArchived) && (x.Name.ToLower().Contains(search) || x.Code.ToLower().Contains(search)))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new DepartmentResponse(x.Id, x.Code, x.Name, x.IsInventoryDepartment, x.IsArchived)).PageAsync(query, ct);
    }
    public Task<PagedResponse<LocationResponse>> GetLocationsPageAsync(PageQuery query, CancellationToken ct)
    {
        QueryPaging.Validate(query); var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        return db.Locations.AsNoTracking().Where(x => (query.IncludeArchived || !x.IsArchived) && (!query.DepartmentId.HasValue || x.DepartmentId == query.DepartmentId) && (x.Name.ToLower().Contains(search) || x.Code.ToLower().Contains(search)))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new LocationResponse(x.Id, x.Code, x.Name, x.DepartmentId, x.Department.Name, x.IsArchived)).PageAsync(query, ct);
    }
    public Task<PagedResponse<EmployeeResponse>> GetEmployeesPageAsync(PageQuery query, CancellationToken ct)
    {
        QueryPaging.Validate(query); var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        return db.Employees.AsNoTracking().Where(x => (query.IncludeArchived || !x.IsArchived) && (x.FullName.ToLower().Contains(search) || x.PersonnelNumber.ToLower().Contains(search)))
            .OrderBy(x => x.FullName).ThenBy(x => x.Id).Select(x => new EmployeeResponse(x.Id, x.PersonnelNumber, x.FullName, x.DepartmentId, x.Department.Name, x.IsMateriallyResponsible, x.IsEmployed, x.IsArchived)).PageAsync(query, ct);
    }
    public Task<PagedResponse<UserResponse>> GetUsersPageAsync(PageQuery query, CancellationToken ct)
    {
        QueryPaging.Validate(query); var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        return db.Users.AsNoTracking().Where(x => x.Login.ToLower().Contains(search) || (x.Employee != null && x.Employee.FullName.ToLower().Contains(search)))
            .OrderBy(x => x.Login).ThenBy(x => x.Id).Select(x => new UserResponse(x.Id, x.Login, x.Role.ToString(), x.EmployeeId, x.Employee == null ? null : x.Employee.FullName, x.IsBlocked, x.CreatedAt)).PageAsync(query, ct);
    }
    private Task<T> Cached<T>(string key, Func<Task<T>> load, CancellationToken ct) =>
        db.Database.CurrentTransaction is not null ? load() : cache.GetAsync("catalog", key, load, ct);

    public async Task<OrganizationResponse?> GetOrganizationAsync(CancellationToken ct) =>
        await Cached("organization", () => db.Organizations.AsNoTracking().Select(x => new OrganizationResponse(x.Id, x.Name, x.Inn, x.Kpp, x.Okpo, x.Address, x.DirectorName)).SingleOrDefaultAsync(ct), ct);

    public async Task<OrganizationResponse> UpsertOrganizationAsync(UpsertOrganizationRequest request, CancellationToken ct)
    {
        Require(request.Name, "Наименование обязательно."); Require(request.Inn, "ИНН обязателен.");
        MaxLength(request.Name, 300, "Наименование");
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Inn.Trim(), @"^(?:[0-9]{10}|[0-9]{12})$")) throw new RequestValidationException("ИНН должен содержать 10 или 12 цифр.");
        if (Clean(request.Kpp) is { } kpp && !System.Text.RegularExpressions.Regex.IsMatch(kpp, @"^[0-9]{9}$")) throw new RequestValidationException("КПП должен содержать 9 цифр.");
        if (Clean(request.Okpo) is { } okpo && !System.Text.RegularExpressions.Regex.IsMatch(okpo, @"^(?:[0-9]{8}|[0-9]{10})$")) throw new RequestValidationException("ОКПО должен содержать 8 или 10 цифр.");
        if (request.Address is not null) MaxLength(request.Address, 500, "Адрес");
        if (request.DirectorName is not null) MaxLength(request.DirectorName, 300, "Руководитель");
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        // A row lock cannot protect the first save when the table is empty.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE organizations IN SHARE ROW EXCLUSIVE MODE", ct);
        var entity = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(ct);
        if (entity is null)
        {
            entity = new Organization { Name = request.Name.Trim(), Inn = request.Inn.Trim() };
            db.Organizations.Add(entity);
        }
        else AttachCurrent(entity);
        entity.Name = request.Name.Trim(); entity.Inn = request.Inn.Trim(); entity.Kpp = Clean(request.Kpp);
        entity.Okpo = Clean(request.Okpo); entity.Address = Clean(request.Address); entity.DirectorName = Clean(request.DirectorName);
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(entity.Id, entity.Name, entity.Inn, entity.Kpp, entity.Okpo, entity.Address, entity.DirectorName);
    }

    public async Task<IReadOnlyList<DepartmentResponse>> GetDepartmentsAsync(bool includeArchived, CancellationToken ct) =>
        await Cached($"departments:{includeArchived}", () => db.Departments.AsNoTracking().Where(x => includeArchived || !x.IsArchived).OrderBy(x => x.Name)
            .Select(x => new DepartmentResponse(x.Id, x.Code, x.Name, x.IsInventoryDepartment, x.IsArchived)).ToListAsync(ct), ct);

    public async Task<DepartmentResponse> GetDepartmentAsync(Guid id, CancellationToken ct) =>
        Map(await LoadCatalogAsync<Department>(id, "Подразделение не найдено.", ct));

    public async Task<DepartmentResponse> CreateDepartmentAsync(UpsertDepartmentRequest request, CancellationToken ct)
    {
        ValidateCodeName(request.Code, request.Name);
        await using var transaction = await BeginAccessChangeAsync(ct);
        if (await db.Departments.AnyAsync(x => x.Code == request.Code.Trim(), ct)) throw new ConflictException("Код подразделения уже используется.");
        var entity = new Department { Code = request.Code.Trim(), Name = request.Name.Trim(), IsInventoryDepartment = request.IsInventoryDepartment };
        db.Departments.Add(entity);
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(entity);
    }

    public async Task<DepartmentResponse> UpdateDepartmentAsync(Guid id, UpsertDepartmentRequest request, CancellationToken ct)
    {
        ValidateCodeName(request.Code, request.Name);
        await using var transaction = await BeginAccessChangeAsync(ct);
        var entity = await LoadCatalogAsync<Department>(id, "Подразделение не найдено.", ct);
        EnsureEditable(entity.IsArchived);
        if (await db.Departments.AnyAsync(x => x.Id != id && x.Code == request.Code.Trim(), ct)) throw new ConflictException("Код подразделения уже используется.");
        if (!request.IsInventoryDepartment)
            await RevokeUsersAsync(await db.Users.Where(x => x.Role == UserRole.InventoryEmployee && x.Employee != null && x.Employee.DepartmentId == id).Select(x => x.Id).ToArrayAsync(ct), ct);
        entity.Code = request.Code.Trim(); entity.Name = request.Name.Trim(); entity.IsInventoryDepartment = request.IsInventoryDepartment;
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(entity);
    }

    public async Task ArchiveDepartmentAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await BeginAccessChangeAsync(ct);
        var entity = await LoadCatalogAsync<Department>(id, "Подразделение не найдено.", ct);
        if (entity.IsArchived) return;
        if (await ActiveAssets.AnyAsync(x => x.DepartmentId == id, ct)) throw new ConflictException("В подразделении есть активные ОС.");
        if (await db.Locations.AnyAsync(x => x.DepartmentId == id && !x.IsArchived, ct)) throw new ConflictException("Сначала архивируйте или перенесите помещения подразделения.");
        if (await db.Employees.AnyAsync(x => x.DepartmentId == id && !x.IsArchived, ct)) throw new ConflictException("Сначала архивируйте или переведите сотрудников подразделения.");
        entity.IsArchived = true; await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<LocationResponse>> GetLocationsAsync(Guid? departmentId, bool includeArchived, CancellationToken ct) =>
        await Cached($"locations:{departmentId?.ToString("N") ?? "all"}:{includeArchived}", () => db.Locations.AsNoTracking().Where(x => (!departmentId.HasValue || x.DepartmentId == departmentId) && (includeArchived || !x.IsArchived))
            .OrderBy(x => x.Name).Select(x => new LocationResponse(x.Id, x.Code, x.Name, x.DepartmentId, x.Department.Name, x.IsArchived)).ToListAsync(ct), ct);

    public async Task<LocationResponse> CreateLocationAsync(UpsertLocationRequest request, CancellationToken ct)
    {
        await using var transaction = await BeginAccessChangeAsync(ct);
        ValidateCodeName(request.Code, request.Name); await EnsureDepartment(request.DepartmentId, ct);
        if (await db.Locations.AnyAsync(x => x.DepartmentId == request.DepartmentId && x.Code == request.Code.Trim(), ct)) throw new ConflictException("Код помещения уже используется.");
        var entity = new Location { Code = request.Code.Trim(), Name = request.Name.Trim(), DepartmentId = request.DepartmentId };
        db.Locations.Add(entity); await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetLocationAsync(entity.Id, ct);
    }

    public async Task<LocationResponse> UpdateLocationAsync(Guid id, UpsertLocationRequest request, CancellationToken ct)
    {
        await using var transaction = await BeginAccessChangeAsync(ct);
        ValidateCodeName(request.Code, request.Name); await EnsureDepartment(request.DepartmentId, ct);
        var entity = await LoadCatalogAsync<Location>(id, "Помещение не найдено.", ct);
        EnsureEditable(entity.IsArchived);
        if (entity.DepartmentId != request.DepartmentId && await ActiveAssets.AnyAsync(x => x.LocationId == id, ct))
            throw new ConflictException("Нельзя перенести помещение в другое подразделение, пока в нем есть активные ОС.");
        if (await db.Locations.AnyAsync(x => x.Id != id && x.DepartmentId == request.DepartmentId && x.Code == request.Code.Trim(), ct)) throw new ConflictException("Код помещения уже используется.");
        entity.Code = request.Code.Trim(); entity.Name = request.Name.Trim(); entity.DepartmentId = request.DepartmentId;
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetLocationAsync(id, ct);
    }

    public async Task ArchiveLocationAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await BeginAccessChangeAsync(ct);
        var entity = await LoadCatalogAsync<Location>(id, "Помещение не найдено.", ct);
        if (entity.IsArchived) return;
        if (await ActiveAssets.AnyAsync(x => x.LocationId == id, ct)) throw new ConflictException("В помещении есть активные ОС.");
        entity.IsArchived = true; await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<EmployeeResponse>> GetEmployeesAsync(bool includeArchived, CancellationToken ct) =>
        await Cached($"employees:{includeArchived}", () => db.Employees.AsNoTracking().Where(x => includeArchived || !x.IsArchived).OrderBy(x => x.FullName)
            .Select(x => new EmployeeResponse(x.Id, x.PersonnelNumber, x.FullName, x.DepartmentId, x.Department.Name, x.IsMateriallyResponsible, x.IsEmployed, x.IsArchived)).ToListAsync(ct), ct);

    public async Task<EmployeeResponse> CreateEmployeeAsync(UpsertEmployeeRequest request, CancellationToken ct)
    {
        await using var transaction = await BeginAccessChangeAsync(ct);
        ValidateEmployee(request); await EnsureDepartment(request.DepartmentId, ct);
        if (await db.Employees.AnyAsync(x => x.PersonnelNumber == request.PersonnelNumber.Trim(), ct)) throw new ConflictException("Номер сотрудника уже используется.");
        var entity = new Employee { PersonnelNumber = request.PersonnelNumber.Trim(), FullName = request.FullName.Trim(), DepartmentId = request.DepartmentId, IsMateriallyResponsible = request.IsMateriallyResponsible, IsEmployed = request.IsEmployed };
        db.Employees.Add(entity); await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetEmployeeAsync(entity.Id, ct);
    }

    public async Task<EmployeeResponse> UpdateEmployeeAsync(Guid id, UpsertEmployeeRequest request, CancellationToken ct)
    {
        await using var transaction = await BeginAccessChangeAsync(ct);
        ValidateEmployee(request); await EnsureDepartment(request.DepartmentId, ct);
        var entity = await LoadCatalogAsync<Employee>(id, "Сотрудник не найден.", ct);
        EnsureEditable(entity.IsArchived);
        if (!request.IsMateriallyResponsible && await ActiveAssets.AnyAsync(x => x.MateriallyResponsibleEmployeeId == id, ct))
            throw new ConflictException("Нельзя снять признак МОЛ, пока за сотрудником закреплены активные ОС.");
        if (await db.Employees.AnyAsync(x => x.Id != id && x.PersonnelNumber == request.PersonnelNumber.Trim(), ct)) throw new ConflictException("Номер сотрудника уже используется.");
        if (!request.IsEmployed) await RevokeEmployeeAccessAsync(id, ct);
        else if (!await db.Departments.AnyAsync(x => x.Id == request.DepartmentId && x.IsInventoryDepartment, ct))
            await RevokeUsersAsync(await db.Users.Where(x => x.EmployeeId == id && x.Role == UserRole.InventoryEmployee).Select(x => x.Id).ToArrayAsync(ct), ct);
        entity.PersonnelNumber = request.PersonnelNumber.Trim(); entity.FullName = request.FullName.Trim(); entity.DepartmentId = request.DepartmentId;
        entity.IsMateriallyResponsible = request.IsMateriallyResponsible; entity.IsEmployed = request.IsEmployed;
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetEmployeeAsync(id, ct);
    }

    public async Task ArchiveEmployeeAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await BeginAccessChangeAsync(ct);
        var entity = await LoadCatalogAsync<Employee>(id, "Сотрудник не найден.", ct);
        if (entity.IsArchived) return;
        if (await ActiveAssets.AnyAsync(x => x.MateriallyResponsibleEmployeeId == id, ct)) throw new ConflictException("За сотрудником закреплены активные ОС.");
        await RevokeEmployeeAccessAsync(id, ct);
        entity.IsArchived = true; entity.IsEmployed = false;
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<UserResponse>> GetUsersAsync(CancellationToken ct) =>
        await Cached("users", () => db.Users.AsNoTracking().OrderBy(x => x.Login).Select(x => new UserResponse(x.Id, x.Login, x.Role.ToString(), x.EmployeeId, x.Employee == null ? null : x.Employee.FullName, x.IsBlocked, x.CreatedAt)).ToListAsync(ct), ct);

    public async Task<UserResponse> GetUserAsync(Guid id, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new UserResponse(x.Id, x.Login, x.Role.ToString(), x.EmployeeId, x.Employee == null ? null : x.Employee.FullName, x.IsBlocked, x.CreatedAt))
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Учетная запись не найдена.");

    public async Task<UserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct)
    {
        Require(request.Login, "Логин обязателен.");
        if (request.Login.Trim().Length > 100) throw new RequestValidationException("Логин должен содержать не более 100 символов.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Login.Trim(), @"^[\p{L}\p{N}._@-]+$")) throw new RequestValidationException("Логин: допустимы буквы, цифры и символы . _ @ -.");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8 || request.Password.Length > 256)
            throw new RequestValidationException("Пароль должен содержать от 8 до 256 символов и не состоять из пробелов.");
        if (!Enum.TryParse<UserRole>(request.Role, true, out var role) || !Enum.IsDefined(role)) throw new RequestValidationException("Неизвестная роль.");
        if (role == UserRole.InventoryEmployee && !request.EmployeeId.HasValue)
            throw new RequestValidationException("Для сотрудника инвентаризации выберите сотрудника организации.");
        await using var transaction = await BeginAccessChangeAsync(ct);
        var normalized = request.Login.Trim().ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.NormalizedLogin == normalized, ct)) throw new ConflictException("Логин уже используется.");
        if (request.EmployeeId.HasValue)
        {
            var employee = await db.Employees.AsNoTracking().Include(x => x.Department).SingleOrDefaultAsync(x => x.Id == request.EmployeeId, ct) ?? throw new NotFoundException("Сотрудник не найден.");
            if (employee.IsArchived || !employee.IsEmployed || employee.Department.IsArchived)
                throw new RequestValidationException("Учетная запись разрешена только для работающего сотрудника активного подразделения.");
            if (role == UserRole.InventoryEmployee && !employee.Department.IsInventoryDepartment)
                throw new RequestValidationException("Для роли инвентаризатора выберите сотрудника отдела инвентаризации.");
        }
        var entity = new UserAccount { Login = request.Login.Trim(), NormalizedLogin = normalized, PasswordHash = string.Empty, Role = role, EmployeeId = request.EmployeeId };
        entity.PasswordHash = passwordHasher.HashPassword(entity, request.Password); db.Users.Add(entity); await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return (await GetUsersAsync(ct)).Single(x => x.Id == entity.Id);
    }

    public async Task<UserResponse> SetUserBlockedAsync(Guid id, bool isBlocked, CancellationToken ct)
    {
        await using var transaction = await BeginAccessChangeAsync(ct);
        var entity = (await LockUsersAsync([id], ct)).SingleOrDefault() ?? throw new NotFoundException("Пользователь не найден.");
        if (!isBlocked && (entity.Role == UserRole.InventoryEmployee && entity.EmployeeId == null || entity.EmployeeId.HasValue && !await db.Employees.AnyAsync(x => x.Id == entity.EmployeeId && !x.IsArchived && x.IsEmployed && !x.Department.IsArchived && (entity.Role != UserRole.InventoryEmployee || x.Department.IsInventoryDepartment), ct)))
            throw new ConflictException("Нельзя разблокировать учетную запись архивного или неработающего сотрудника.");
        if (isBlocked) await RevokeUsersAsync([id], ct);
        else entity.IsBlocked = false;
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return (await GetUsersAsync(ct)).Single(x => x.Id == id);
    }

    private async Task RevokeEmployeeAccessAsync(Guid employeeId, CancellationToken ct)
    {
        await RevokeUsersAsync(await db.Users.Where(x => x.EmployeeId == employeeId).Select(x => x.Id).ToArrayAsync(ct), ct);
    }

    // All account/employee access changes share a transaction lock, so concurrent administrators
    // cannot both remove the other account after independently observing a backup administrator.
    private Task<IDbContextTransaction?> BeginAccessChangeAsync(CancellationToken ct) => RegistryWriteGuard.BeginAsync(db, false, ct);

    private IQueryable<Asset> ActiveAssets => db.Assets.Where(x => x.ArchivedAt == null && x.Status != AssetStatus.Archived);

    private async Task<T> LoadCatalogAsync<T>(Guid id, string missing, CancellationToken ct) where T : Entity
    {
        // A waiting command must use committed values, not an earlier tracked
        // copy from this scope, when checking whether a row is archived.
        var entity = await db.Set<T>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(missing);
        AttachCurrent(entity);
        return entity;
    }

    private void AttachCurrent<T>(T entity) where T : Entity
    {
        var tracked = db.Set<T>().Local.SingleOrDefault(x => x.Id == entity.Id);
        if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
        db.Set<T>().Attach(entity);
    }

    private async Task<List<UserAccount>> LockUsersAsync(Guid[] ids, CancellationToken ct)
    {
        var users = await db.Users.FromSqlInterpolated($"SELECT * FROM user_accounts WHERE id = ANY({ids}) ORDER BY id FOR NO KEY UPDATE").AsNoTracking().ToListAsync(ct);
        foreach (var user in users)
        {
            var tracked = db.Users.Local.SingleOrDefault(x => x.Id == user.Id);
            if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
            db.Users.Attach(user);
        }
        return users;
    }

    private async Task RevokeUsersAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return;
        var users = await LockUsersAsync(ids, ct);
        var activeAdmins = db.Users.Where(x => x.Role == UserRole.Administrator && !x.IsBlocked && (x.EmployeeId == null || x.Employee != null && !x.Employee.IsArchived && x.Employee.IsEmployed && !x.Employee.Department.IsArchived));
        if (await activeAdmins.AnyAsync(x => ids.Contains(x.Id), ct) && !await activeAdmins.AnyAsync(x => !ids.Contains(x.Id), ct))
            throw new ConflictException("Нельзя отключить последнего активного администратора. Сначала создайте другого администратора.");
        foreach (var user in users) { user.IsBlocked = true; user.SecurityVersion++; }
        var tokens = await db.RefreshTokens.Where(x => ids.Contains(x.UserId) && x.RevokedAt == null).ToListAsync(ct);
        foreach (var token in tokens) token.RevokedAt = DateTimeOffset.UtcNow;
    }
    private static void EnsureEditable(bool archived) { if (archived) throw new ConflictException("Архивную запись нельзя редактировать."); }
    private async Task EnsureDepartment(Guid id, CancellationToken ct) { if (!await db.Departments.AnyAsync(x => x.Id == id && !x.IsArchived, ct)) throw new NotFoundException("Подразделение не найдено."); }
    public async Task<LocationResponse> GetLocationAsync(Guid id, CancellationToken ct) => await db.Locations.AsNoTracking().Where(x => x.Id == id).Select(x => new LocationResponse(x.Id, x.Code, x.Name, x.DepartmentId, x.Department.Name, x.IsArchived)).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Помещение не найдено.");
    public async Task<EmployeeResponse> GetEmployeeAsync(Guid id, CancellationToken ct) => await db.Employees.AsNoTracking().Where(x => x.Id == id).Select(x => new EmployeeResponse(x.Id, x.PersonnelNumber, x.FullName, x.DepartmentId, x.Department.Name, x.IsMateriallyResponsible, x.IsEmployed, x.IsArchived)).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Сотрудник не найден.");
    private static DepartmentResponse Map(Department x) => new(x.Id, x.Code, x.Name, x.IsInventoryDepartment, x.IsArchived);
    private static void ValidateCodeName(string code, string name) { Require(code, "Код обязателен."); Require(name, "Наименование обязательно."); MaxLength(code, 50, "Код"); MaxLength(name, 250, "Наименование"); }
    private static void ValidateEmployee(UpsertEmployeeRequest request) { Require(request.PersonnelNumber, "Номер сотрудника обязателен."); Require(request.FullName, "ФИО обязательно."); MaxLength(request.PersonnelNumber, 50, "Номер сотрудника"); MaxLength(request.FullName, 300, "ФИО"); }
    private static void MaxLength(string value, int max, string field) { if (value.Trim().Length > max) throw new RequestValidationException($"{field}: не более {max} символов."); }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void Require(string value, string message) { if (string.IsNullOrWhiteSpace(value)) throw new RequestValidationException(message); }
}
