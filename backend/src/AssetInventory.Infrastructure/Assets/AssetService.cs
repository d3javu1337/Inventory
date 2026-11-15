using AssetInventory.Application.Assets;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Assets;
using AssetInventory.Contracts.Common;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AssetInventory.Infrastructure.Assets;

internal sealed class AssetService(AppDbContext db) : IAssetService
{
    public async Task<PagedResponse<AssetResponse>> GetAsync(AssetListQuery query, CancellationToken ct)
    {
        var paging = new PageQuery(query.Page, query.PageSize, query.Search);
        QueryPaging.Validate(paging);
        var assets = db.Assets.AsNoTracking().Where(x => query.IncludeArchived || x.ArchivedAt == null && x.Status != AssetStatus.Archived);
        if (!string.IsNullOrWhiteSpace(query.Search)) { var search = query.Search.Trim().ToLower(); assets = assets.Where(x => x.Name.ToLower().Contains(search) || x.InventoryNumber.ToLower().Contains(search)); }
        if (query.DepartmentId.HasValue) assets = assets.Where(x => x.DepartmentId == query.DepartmentId);
        if (query.MateriallyResponsibleEmployeeId.HasValue) assets = assets.Where(x => x.MateriallyResponsibleEmployeeId == query.MateriallyResponsibleEmployeeId);
        return await Project(assets.OrderBy(x => x.InventoryNumber).ThenBy(x => x.Id)).PageAsync(paging, ct);
    }

    public async Task<AssetResponse> GetByIdAsync(Guid id, CancellationToken ct) =>
        await Project(db.Assets.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Основное средство не найдено.");

    public async Task<AssetResponse> CreateAsync(UpsertAssetRequest request, CancellationToken ct)
    {
        await using var transaction = await RegistryWriteGuard.BeginAsync(db, true, ct);
        await Validate(request, null, ct);
        var entity = new Asset
        {
            InventoryNumber = request.InventoryNumber.Trim(), Name = request.Name.Trim(), AccountingAccount = request.AccountingAccount.Trim(),
            SerialNumber = Clean(request.SerialNumber), ManufactureYear = request.ManufactureYear, AcceptedAt = request.AcceptedAt,
            InitialCost = request.InitialCost, DepartmentId = request.DepartmentId, LocationId = request.LocationId,
            MateriallyResponsibleEmployeeId = request.MateriallyResponsibleEmployeeId
        };
        db.Assets.Add(entity);
        db.AssetAssignmentHistory.Add(new AssetAssignmentHistory { AssetId = entity.Id, DepartmentId = entity.DepartmentId, LocationId = entity.LocationId, MateriallyResponsibleEmployeeId = entity.MateriallyResponsibleEmployeeId, ValidFrom = DateTimeOffset.UtcNow });
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetByIdAsync(entity.Id, ct);
    }

    public async Task<AssetResponse> UpdateAsync(Guid id, long expectedVersion, UpsertAssetRequest request, CancellationToken ct)
    {
        await using var transaction = await RegistryWriteGuard.BeginAsync(db, true, ct);
        var entity = await db.Assets.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Основное средство не найдено.");
        if (entity.Version != expectedVersion) throw StaleAsset();
        if (entity.ArchivedAt is not null || entity.Status == AssetStatus.Archived) throw new ConflictException("Архивное ОС нельзя редактировать.");
        await Validate(request, id, ct);
        var assignmentChanged = entity.DepartmentId != request.DepartmentId || entity.LocationId != request.LocationId || entity.MateriallyResponsibleEmployeeId != request.MateriallyResponsibleEmployeeId;
        if (assignmentChanged)
        {
            var active = await db.AssetAssignmentHistory.SingleOrDefaultAsync(x => x.AssetId == id && x.ValidTo == null, ct);
            if (active is not null) active.ValidTo = DateTimeOffset.UtcNow;
            db.AssetAssignmentHistory.Add(new AssetAssignmentHistory { AssetId = id, DepartmentId = request.DepartmentId, LocationId = request.LocationId, MateriallyResponsibleEmployeeId = request.MateriallyResponsibleEmployeeId, ValidFrom = DateTimeOffset.UtcNow });
        }
        entity.InventoryNumber = request.InventoryNumber.Trim(); entity.Name = request.Name.Trim(); entity.AccountingAccount = request.AccountingAccount.Trim();
        entity.SerialNumber = Clean(request.SerialNumber); entity.ManufactureYear = request.ManufactureYear; entity.AcceptedAt = request.AcceptedAt; entity.InitialCost = request.InitialCost;
        entity.DepartmentId = request.DepartmentId; entity.LocationId = request.LocationId; entity.MateriallyResponsibleEmployeeId = request.MateriallyResponsibleEmployeeId; entity.Version++;
        await db.SaveCommandAsync(ct, StaleAsset);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    private static PreconditionFailedException StaleAsset() => new("Карточка была изменена другим пользователем.");

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await RegistryWriteGuard.BeginAsync(db, true, ct);
        var entity = await db.Assets.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Основное средство не найдено.");
        if (entity.ArchivedAt is not null) return;
        entity.Status = AssetStatus.Archived; entity.ArchivedAt = DateTimeOffset.UtcNow; entity.Version++;
        var assignment = await db.AssetAssignmentHistory.SingleOrDefaultAsync(x => x.AssetId == id && x.ValidTo == null, ct);
        if (assignment is not null) assignment.ValidTo = entity.ArchivedAt;
        await db.SaveCommandAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task<PagedResponse<AssetAssignmentHistoryResponse>> GetHistoryPageAsync(Guid id, PageQuery query, CancellationToken ct)
    {
        QueryPaging.Validate(query);
        if (!await db.Assets.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Основное средство не найдено.");
        var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        var rows = from entry in db.AssetAssignmentHistory.AsNoTracking().Where(x => x.AssetId == id)
                   join department in db.Departments.AsNoTracking() on entry.DepartmentId equals department.Id
                   join location in db.Locations.AsNoTracking() on entry.LocationId equals location.Id
                   join employee in db.Employees.AsNoTracking() on entry.MateriallyResponsibleEmployeeId equals employee.Id
                   where department.Name.ToLower().Contains(search) || location.Name.ToLower().Contains(search) || employee.FullName.ToLower().Contains(search)
                   orderby entry.ValidFrom descending, entry.Id
                   select new AssetAssignmentHistoryResponse(entry.Id, entry.DepartmentId, department.Name, entry.LocationId, location.Name, entry.MateriallyResponsibleEmployeeId, employee.FullName, entry.ValidFrom, entry.ValidTo);
        return await rows.PageAsync(query, ct);
    }

    public async Task<IReadOnlyList<AssetAssignmentHistoryResponse>> GetHistoryAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Assets.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Основное средство не найдено.");
        var history = db.AssetAssignmentHistory.AsNoTracking().Where(x => x.AssetId == id);
        return await (
            from entry in history
            join department in db.Departments.AsNoTracking() on entry.DepartmentId equals department.Id
            join location in db.Locations.AsNoTracking() on entry.LocationId equals location.Id
            join employee in db.Employees.AsNoTracking() on entry.MateriallyResponsibleEmployeeId equals employee.Id
            orderby entry.ValidFrom descending
            select new AssetAssignmentHistoryResponse(
                entry.Id,
                entry.DepartmentId,
                department.Name,
                entry.LocationId,
                location.Name,
                entry.MateriallyResponsibleEmployeeId,
                employee.FullName,
                entry.ValidFrom,
                entry.ValidTo)).ToListAsync(ct);
    }

    private async Task Validate(UpsertAssetRequest request, Guid? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InventoryNumber) || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.AccountingAccount)) throw new RequestValidationException("Инвентарный номер, наименование и счет обязательны.");
        if (request.InitialCost < 0) throw new RequestValidationException("Стоимость не может быть отрицательной.");
        if (!AssetCosts.IsSupported(request.InitialCost)) throw new RequestValidationException("Стоимость должна помещаться в numeric(19,2), максимум два знака после запятой.");
        if (request.InventoryNumber.Trim().Length > 100 || request.Name.Trim().Length > 500 || request.AccountingAccount.Trim().Length > 50 || request.SerialNumber?.Length > 200) throw new RequestValidationException("Превышена допустимая длина поля ОС.");
        if (request.ManufactureYear is int year && (year < 1900 || year > DateTimeOffset.UtcNow.Year + 1)) throw new RequestValidationException("Некорректный год выпуска.");
        if (!await db.Departments.AnyAsync(x => x.Id == request.DepartmentId && !x.IsArchived, ct)) throw new RequestValidationException("Выберите активное подразделение.");
        if (await db.Assets.AnyAsync(x => x.Id != id && x.InventoryNumber == request.InventoryNumber.Trim(), ct)) throw new ConflictException("Инвентарный номер уже используется.");
        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.LocationId && !x.IsArchived, ct) ?? throw new NotFoundException("Помещение не найдено.");
        if (location.DepartmentId != request.DepartmentId) throw new RequestValidationException("Помещение не относится к выбранному подразделению.");
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.MateriallyResponsibleEmployeeId && !x.IsArchived, ct) ?? throw new NotFoundException("МОЛ не найдено.");
        if (!employee.IsMateriallyResponsible || !employee.IsEmployed) throw new RequestValidationException("Выберите работающего сотрудника с признаком МОЛ.");
    }

    private static IQueryable<AssetResponse> Project(IQueryable<Asset> source) => source.Select(x => new AssetResponse(x.Id, x.InventoryNumber, x.Name, x.AccountingAccount, x.SerialNumber, x.ManufactureYear, x.AcceptedAt, x.InitialCost, x.DepartmentId, x.Department.Name, x.LocationId, x.Location.Name, x.MateriallyResponsibleEmployeeId, x.MateriallyResponsibleEmployee.FullName, x.Status.ToString(), x.ArchivedAt, x.Version));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
