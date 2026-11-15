using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Common;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AssetInventory.Infrastructure.Inventories;

internal sealed class InventoryService(AppDbContext db, IPasswordHasher<UserAccount> passwordHasher, IDocumentService documents) : IInventoryService
{
    public async Task<PagedResponse<InventorySummaryResponse>> GetAsync(InventoryListQuery query, Guid? assignedUserId, CancellationToken ct)
    {
        var paging = new PageQuery(query.Page, query.PageSize);
        QueryPaging.Validate(paging);
        var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        if (search.Length > 200) throw new RequestValidationException("Поисковый запрос не может быть длиннее 200 символов.");
        var source = db.Inventories.AsNoTracking().AsQueryable();
        if (assignedUserId is not null) source = source.Where(x => x.Assignments.Any(m => m.UserId == assignedUserId));
        if (search.Length > 0)
        {
            var inventoryId = Guid.TryParse(search, out var parsedId) ? parsedId : Guid.Empty;
            source = source.Where(x => x.Id == inventoryId || x.Number.ToLower().Contains(search)
                || x.ReasonDetails != null && x.ReasonDetails.ToLower().Contains(search)
                || db.Employees.Any(e => e.Id == x.TargetEmployeeId && e.FullName.ToLower().Contains(search))
                || x.Assignments.Any(m => m.User.Login.ToLower().Contains(search)
                    || m.User.Employee != null && m.User.Employee.FullName.ToLower().Contains(search)));
        }
        if (!string.IsNullOrWhiteSpace(query.Reason))
        {
            if (!Enum.TryParse<InventoryReason>(query.Reason, true, out var reason) || !Enum.IsDefined(reason)) throw new RequestValidationException("Некорректное основание проверки.");
            source = source.Where(x => x.Reason == reason);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<InventoryStatus>(query.Status, true, out var status) || !Enum.IsDefined(status)) throw new RequestValidationException("Некорректный статус проверки.");
            source = source.Where(x => x.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            if (!Enum.TryParse<InventoryType>(query.Type, true, out var type) || !Enum.IsDefined(type)) throw new RequestValidationException("Некорректный тип проверки.");
            source = source.Where(x => x.Type == type);
        }
        return await source.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new InventorySummaryResponse(x.Id, x.Number, x.Type.ToString(), x.Reason.ToString(), x.Status.ToString(), x.PlannedAt,
                x.Items.Count, x.Items.Count(i => i.Result == ScanResult.Found), x.Items.Count(i => i.Result == ScanResult.NotFound), x.Items.Count(i => i.Result == ScanResult.Pending),
                x.Assignments.OrderBy(m => m.Role).Select(m => m.User.Employee == null ? m.User.Login : m.User.Employee.FullName).FirstOrDefault(),
                db.Employees.Where(e => e.Id == x.TargetEmployeeId).Select(e => e.FullName).FirstOrDefault())).PageAsync(paging, ct);
    }

    public async Task<InventoryDetailResponse> GetByIdAsync(Guid id, Guid? assignedUserId, CancellationToken ct)
    {
        var entity = await LoadAsync(id, ct);
        if (assignedUserId is not null && entity.Assignments.All(x => x.UserId != assignedUserId)) throw new NotFoundException("Инвентаризация не найдена.");
        return await MapAsync(entity, ct);
    }

    public async Task<InventoryDetailResponse> GetPageAsync(Guid id, InventoryDetailQuery query, Guid? assignedUserId, CancellationToken ct)
    {
        if (query.Page < 1 || query.Page > 1_000_000 || query.PageSize < 1 || query.PageSize > 200)
            throw new RequestValidationException("Страница должна быть положительной, размер — от 1 до 200.");
        var filter = query.Filter?.ToLowerInvariant() ?? "all";
        if (filter is not ("all" or "remaining" or "found" or "issues" or "unexpected")) throw new RequestValidationException("Неизвестный фильтр результатов.");
        var search = query.Search?.Trim() ?? "";
        if (search.Length > 200) throw new RequestValidationException("Поисковый запрос не может быть длиннее 200 символов.");
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct) : null;
        var source = db.Inventories.AsNoTracking().Where(x => x.Id == id);
        if (assignedUserId is not null) source = source.Where(x => x.Assignments.Any(m => m.UserId == assignedUserId));
        var x = await source.Include(i => i.Assignments).ThenInclude(m => m.User).ThenInclude(u => u.Employee).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Инвентаризация не найдена.");
        var counts = await source.Select(i => new InventoryCounts(i.Items.Count, i.Items.Count(a => a.Result == ScanResult.Found),
            i.Items.Count(a => a.Result == ScanResult.Pending), i.Items.Count(a => a.Result == ScanResult.NotFound),
            i.Items.Count(a => a.Result == ScanResult.Found && (a.Condition == AssetCondition.Damaged || a.Condition == AssetCondition.Unusable)),
            i.Items.Count(a => a.Result == ScanResult.NotFound || a.Condition == AssetCondition.Damaged || a.Condition == AssetCondition.Unusable),
            db.UnexpectedScans.Count(s => s.InventoryId == id))).SingleAsync(ct);
        var items = db.Set<InventoryAssetSnapshot>().AsNoTracking().Where(i => i.InventoryId == id);
        items = filter switch {
            "remaining" => items.Where(i => i.Result != ScanResult.Found),
            "found" => items.Where(i => i.Result == ScanResult.Found),
            "issues" => items.Where(i => i.Result == ScanResult.NotFound || i.Condition == AssetCondition.Damaged || i.Condition == AssetCondition.Unusable),
            "unexpected" => items.Where(i => false), _ => items
        };
        if (search.Length > 0) {
            var lowered = search.ToLowerInvariant();
            var assetId = Guid.TryParse(search, out var parsedId) ? parsedId : Guid.Empty;
            items = items.Where(i => i.InventoryNumber.ToLower().Contains(lowered) || i.Name.ToLower().Contains(lowered) || i.DepartmentName.ToLower().Contains(lowered) || i.LocationName.ToLower().Contains(lowered) || i.AssetId == assetId);
        }
        var total = await items.CountAsync(ct);
        var scans = from scan in db.UnexpectedScans.AsNoTracking().Where(s => s.InventoryId == id)
                    join asset in db.Assets.AsNoTracking() on scan.RecognizedAssetId equals asset.Id into recognized
                    from asset in recognized.DefaultIfEmpty()
                    select new { Scan = scan, Number = asset == null ? null : asset.InventoryNumber, Name = asset == null ? null : asset.Name };
        if (search.Length > 0) {
            var lowered = search.ToLowerInvariant();
            scans = scans.Where(s => s.Scan.RawValue.ToLower().Contains(lowered) || (s.Number != null && s.Number.ToLower().Contains(lowered)) || (s.Name != null && s.Name.ToLower().Contains(lowered)));
        }
        var unexpectedTotal = await scans.CountAsync(ct);
        var selectedTotal = filter == "unexpected" ? unexpectedTotal : total;
        var page = Math.Min(query.Page, Math.Max(1, (int)Math.Ceiling(selectedTotal / (double)query.PageSize)));
        var rows = await items.OrderBy(i => i.InventoryNumber).ThenBy(i => i.Id).Skip((page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        var unexpected = filter == "unexpected" ? await scans.OrderBy(s => s.Scan.ScannedAt).ThenBy(s => s.Scan.Id).Skip((page - 1) * query.PageSize).Take(query.PageSize)
            .Select(s => new UnexpectedScanResponse(s.Scan.Id, s.Scan.RawValue, s.Scan.Type.ToString(), s.Scan.RecognizedAssetId, s.Number, s.Name, s.Scan.ScannedAt)).ToListAsync(ct) : [];
        var sourceNumber = x.SourceInventoryId is { } sourceId ? await db.Inventories.AsNoTracking().Where(i => i.Id == sourceId).Select(i => i.Number).SingleAsync(ct) : null;
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(x.Id, x.Number, x.Type.ToString(), x.Reason.ToString(), x.ReasonDetails, x.Status.ToString(), x.TargetEmployeeId, x.PlannedAt, x.StartedAt, x.ScanningFinishedAt, x.CompletedAt,
            rows.Select(MapItem).ToList(), x.Assignments.Select(m => new InventoryAssigneeResponse(m.Id, m.UserId, m.User.Login, m.User.Employee?.FullName, m.Role.ToString(), m.ConfirmationStatus.ToString(), m.ConfirmedAt, m.RejectionReason)).ToList(), unexpected,
            x.SourceInventoryId, sourceNumber, counts, page, query.PageSize, total, unexpectedTotal, x.Round);
    }

    public async Task<InventoryDetailResponse> CreateAsync(CreateInventoryRequest request, Guid actorId, CancellationToken ct)
    {
        if (!Enum.TryParse<InventoryType>(request.Type, true, out var type) || !Enum.IsDefined(type)) throw new RequestValidationException("Некорректный тип инвентаризации.");
        if (!Enum.TryParse<InventoryReason>(request.Reason, true, out var reason) || !Enum.IsDefined(reason)) throw new RequestValidationException("Некорректное основание.");
        if (request.PlannedAt == default) throw new RequestValidationException("Укажите плановую дату проверки.");
        if (request.ReasonDetails?.Trim().Length > 1000) throw new RequestValidationException("Описание основания не может быть длиннее 1000 символов.");
        if (reason == InventoryReason.Other && string.IsNullOrWhiteSpace(request.ReasonDetails)) throw new RequestValidationException("Для иного основания требуется описание.");
        if (type == InventoryType.EventDriven && (request.TargetEmployeeId is null || request.TargetEmployeeId == Guid.Empty)) throw new RequestValidationException("Для событийной проверки требуется МОЛ.");
        var inventory = new Inventory { Number = NewNumber(), Type = type, Reason = reason, ReasonDetails = request.ReasonDetails?.Trim(), TargetEmployeeId = request.TargetEmployeeId, PlannedAt = request.PlannedAt.ToUniversalTime(), CreatedByUserId = actorId };
        await AssignIfAvailableAsync(db, inventory, ct);
        db.Inventories.Add(inventory); AddAudit(actorId, "inventory.created", inventory);
        await db.SaveChangesAsync(ct); return await GetByIdAsync(inventory.Id, null, ct);
    }

    public async Task<InventoryDetailResponse> CloneAsync(Guid id, CloneInventoryRequest request, Guid actorId, CancellationToken ct)
    {
        if (request.PlannedAt == default) throw new RequestValidationException("Укажите плановую дату новой проверки.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var source = await LoadLockedAsync(id, ct);
        if (source.Status is not (InventoryStatus.Cancelled or InventoryStatus.Completed))
            throw new ConflictException("Создать новую проверку на основе существующей можно только после отмены или завершения.");
        var clone = new Inventory
        {
            Number = NewNumber(), Type = source.Type, Reason = source.Reason, ReasonDetails = source.ReasonDetails,
            TargetEmployeeId = source.TargetEmployeeId, PlannedAt = request.PlannedAt.ToUniversalTime(),
            CreatedByUserId = actorId, SourceInventoryId = source.Id,
        };
        // Copy the scope rule, never observations, frozen reports or schedule/event ownership.
        // A fresh accounting snapshot is taken only when the administrator starts this draft.
        await AssignIfAvailableAsync(db, clone, ct);
        db.Inventories.Add(clone);
        AddAudit(actorId, "inventory.cloned", clone, new { sourceInventoryId = source.Id, sourceInventoryNumber = source.Number });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetByIdAsync(clone.Id, null, ct);
    }

    public async Task<InventoryDetailResponse> StartAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        // Reuse the worker transaction and keep assignee eligibility stable until commit.
        await using var transaction = await RegistryWriteGuard.BeginAsync(db, true, ct);
        var inventory = await LoadLockedAsync(id, ct);
        if (inventory.Status is not (InventoryStatus.Draft or InventoryStatus.ReviewRequired or InventoryStatus.NeedsAttention)) throw new ConflictException("Запустить можно только черновик или проверку, требующую исправления.");
        var restarting = inventory.Status == InventoryStatus.ReviewRequired;
        var candidates = await Candidates(db, inventory.TargetEmployeeId).ToListAsync(ct);
        if (candidates.Count == 0) throw new ConflictException("Для запуска нужен активный сотрудник отдела инвентаризации.");
        if (inventory.Assignments.Any(member => candidates.All(candidate => candidate.Id != member.UserId)))
            throw new ConflictException("Назначенный исполнитель недоступен. Замените его до запуска или создайте новую проверку.");
        if (inventory.Items.Count == 0)
        {
            var assets = db.Assets.AsNoTracking().Include(x => x.Department).Include(x => x.Location).Include(x => x.MateriallyResponsibleEmployee).Where(x => x.ArchivedAt == null && x.Status != AssetStatus.Archived);
            if (inventory.Type == InventoryType.EventDriven && inventory.TargetEmployeeId is not null) assets = assets.Where(x => x.MateriallyResponsibleEmployeeId == inventory.TargetEmployeeId);
            foreach (var asset in await assets.ToListAsync(ct))
            {
                var snapshot = new InventoryAssetSnapshot
                {
                    InventoryId = inventory.Id,
                    AssetId = asset.Id,
                    InventoryNumber = asset.InventoryNumber,
                    Name = asset.Name,
                    AccountingAccount = asset.AccountingAccount,
                    SerialNumber = asset.SerialNumber,
                    ManufactureYear = asset.ManufactureYear,
                    AcceptedAt = asset.AcceptedAt,
                    InitialCost = asset.InitialCost,
                    DepartmentId = asset.DepartmentId,
                    DepartmentName = asset.Department.Name,
                    LocationId = asset.LocationId,
                    LocationName = asset.Location.Name,
                    MateriallyResponsibleEmployeeId = asset.MateriallyResponsibleEmployeeId,
                    MateriallyResponsibleFullName = asset.MateriallyResponsibleEmployee.FullName
                };
                inventory.Items.Add(snapshot);
                // IDs are assigned by the domain constructor, so explicitly mark
                // new children Added instead of relying on navigation discovery.
                db.InventoryItems.Add(snapshot);
            }
        }
        if (inventory.Items.Count == 0) throw new ConflictException("В область проверки не входит ни одного активного ОС.");
        if (inventory.Assignments.Count == 0)
        {
            var selected = candidates[Random.Shared.Next(candidates.Count)];
            var assignment = new InventoryAssignment { InventoryId = inventory.Id, UserId = selected.Id, Role = CommissionRole.Chairperson };
            inventory.Assignments.Add(assignment); db.InventoryAssignments.Add(assignment);
        }
        else if (inventory.Assignments.Count > 1)
        {
            // Older drafts may still contain a three-person commission. Keep its
            // chairperson as the sole assignee when the inventory is next started.
            var assignee = inventory.Assignments.FirstOrDefault(x => x.Role == CommissionRole.Chairperson)
                ?? inventory.Assignments.First();
            var obsoleteAssignments = inventory.Assignments.Where(x => x.Id != assignee.Id).ToArray();
            db.InventoryAssignments.RemoveRange(obsoleteAssignments);
            foreach (var obsoleteAssignment in obsoleteAssignments) inventory.Assignments.Remove(obsoleteAssignment);
            assignee.Role = CommissionRole.Chairperson;
        }
        if (restarting)
        {
            AddAudit(actorId, "inventory.observations-reset", inventory, inventory.Items.Select(item => new
            {
                item.AssetId, item.Result, item.Condition, item.Comment, item.ScannedAt, item.ScannedByUserId
            }).ToArray());
            // Keep the immutable asset scope, but collect all observations anew.
            foreach (var item in inventory.Items)
            {
                item.Result = ScanResult.Pending; item.Condition = null; item.Comment = null;
                item.ScannedAt = null; item.ScannedByUserId = null;
            }
            var unexpected = await db.UnexpectedScans.Where(scan => scan.InventoryId == id).ToListAsync(ct);
            if (unexpected.Count > 0)
            {
                AddAudit(actorId, "inventory.unexpected-observations-reset", inventory, unexpected.Select(scan => new
                {
                    scan.Id, scan.RawValue, scan.Type, scan.RecognizedAssetId, scan.ScannedAt, scan.ScannedByUserId
                }).ToArray());
                db.UnexpectedScans.RemoveRange(unexpected);
            }
        }
        inventory.Status = InventoryStatus.Active; inventory.StartedAt = DateTimeOffset.UtcNow;
        inventory.Round++;
        inventory.ScanningFinishedAt = null; inventory.CompletedAt = null;
        foreach (var member in inventory.Assignments) { member.ConfirmationStatus = ConfirmationStatus.Pending; member.ConfirmedAt = null; member.RejectionReason = null; }
        AddAudit(actorId, "inventory.started", inventory, new { items = inventory.Items.Count, assignee = inventory.Assignments.Single().UserId });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException exception)
        {
            var entities = string.Join(", ", exception.Entries.Select(x => x.Metadata.ClrType.Name));
            throw new ConflictException($"Данные были изменены параллельно ({entities}). Повторите запуск.");
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetByIdAsync(id, null, ct);
    }

    public async Task<InventoryDetailResponse> CancelAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var x = await LoadLockedAsync(id, ct); if (x.Status is InventoryStatus.Completed or InventoryStatus.Cancelled) throw new ConflictException("Завершенную или отмененную проверку изменить нельзя.");
        x.Status = InventoryStatus.Cancelled; AddAudit(actorId, "inventory.cancelled", x); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return await GetByIdAsync(id, null, ct);
    }

    public async Task<InventoryDetailResponse> ReplaceAssigneeAsync(Guid id, ReplaceAssigneeRequest request, Guid actorId, CancellationToken ct)
    {
        await using var transaction = await RegistryWriteGuard.BeginAsync(db, true, ct);
        var x = await LoadLockedAsync(id, ct); if (x.Status is not (InventoryStatus.Draft or InventoryStatus.NeedsAttention or InventoryStatus.ReviewRequired)) throw new ConflictException("Исполнителя можно менять до запуска или перед повторным обходом.");
        var current = x.Assignments.SingleOrDefault(m => m.UserId == request.OldUserId);
        if (current is null && (x.Assignments.Count != 0 || request.OldUserId != Guid.Empty)) throw new NotFoundException("Назначенный сотрудник не найден.");
        if (x.Assignments.Any(m => m.UserId == request.NewUserId)) throw new ConflictException("Пользователь уже назначен на эту инвентаризацию.");
        var eligible = await db.Users.Include(u => u.Employee).ThenInclude(e => e!.Department).AnyAsync(u => u.Id == request.NewUserId && !u.IsBlocked && u.Role == UserRole.InventoryEmployee && u.Employee != null && !u.Employee.IsArchived && u.Employee.IsEmployed && !u.Employee.Department.IsArchived && u.Employee.Department.IsInventoryDepartment && u.EmployeeId != x.TargetEmployeeId, ct);
        if (!eligible) throw new RequestValidationException("Новый сотрудник не может быть назначен на инвентаризацию.");
        if (current is null)
        {
            var assignment = new InventoryAssignment { InventoryId = x.Id, UserId = request.NewUserId, Role = CommissionRole.Chairperson };
            x.Assignments.Add(assignment); db.InventoryAssignments.Add(assignment);
        }
        else current.UserId = request.NewUserId;
        AddAudit(actorId, "inventory.commission-replaced", x, request);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetByIdAsync(id, null, ct);
    }

    public async Task<ScanAssetResponse> ScanAsync(Guid id, ScanAssetRequest request, Guid actorId, CancellationToken ct, int? expectedRound = null)
    {
        if (string.IsNullOrWhiteSpace(request.QrValue) || request.QrValue.Trim().Length > 500)
            throw new RequestValidationException("QR обязателен и не может быть длиннее 500 символов.");
        if (request.Comment?.Trim().Length > 2000) throw new RequestValidationException("Комментарий не может быть длиннее 2000 символов.");
        if (request.QrValue.Contains('\0') || request.Comment?.Contains('\0') == true)
            throw new RequestValidationException("QR и комментарий не должны содержать нулевой символ.");
        var validAssetId = Guid.TryParse(request.QrValue.Trim(), out var assetId);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var inventory = await LoadLockedAsync(id, ct, includeItems: false);
        EnsureAssignee(inventory, actorId, expectedRound);
        var matchingItems = db.InventoryItems.Where(x => x.InventoryId == id && x.AssetId == assetId);
        // Retries of the final scan remain idempotent even after automatic closure.
        if (inventory.Status == InventoryStatus.AwaitingConfirmation && validAssetId)
        {
            var duplicate = await matchingItems.AsNoTracking().SingleOrDefaultAsync(x => x.Result == ScanResult.Found, ct);
            if (duplicate is not null) return new("Found", true, MapItem(duplicate), "ОС уже было отсканировано.");
        }
        if (inventory.Status != InventoryStatus.Active) throw new ConflictException("Сканирование доступно только для активной проверки.");
        if (!Enum.TryParse<AssetCondition>(request.Condition, true, out var condition) || !Enum.IsDefined(condition)) throw new RequestValidationException("Некорректное состояние ОС.");
        if (condition is AssetCondition.Damaged or AssetCondition.Unusable && string.IsNullOrWhiteSpace(request.Comment)) throw new RequestValidationException("Для поврежденного или непригодного ОС требуется комментарий.");
        async Task<ScanAssetResponse> UnexpectedAsync(UnexpectedScanType type, Guid? recognizedId)
        {
            var response = await SaveUnexpectedAsync(inventory, actorId, request.QrValue, type, recognizedId, ct);
            await transaction.CommitAsync(ct); return response;
        }
        if (!validAssetId) return await UnexpectedAsync(UnexpectedScanType.UnknownQr, null);
        var assetExists = await db.Assets.AnyAsync(x => x.Id == assetId, ct);
        if (!assetExists) return await UnexpectedAsync(UnexpectedScanType.UnknownQr, null);
        var item = await matchingItems.SingleOrDefaultAsync(ct);
        if (item is null) return await UnexpectedAsync(UnexpectedScanType.OutOfScope, assetId);
        if (item.Result == ScanResult.Found) return new("Found", true, MapItem(item), "ОС уже было отсканировано.");
        item.Result = ScanResult.Found; item.Condition = condition; item.Comment = request.Comment?.Trim(); item.ScannedAt = DateTimeOffset.UtcNow; item.ScannedByUserId = actorId;
        AddAudit(actorId, "inventory.asset-scanned", inventory, new { assetId, condition });
        if (!await db.InventoryItems.AnyAsync(x => x.InventoryId == id && x.Id != item.Id && x.Result != ScanResult.Found, ct))
        {
            inventory.Status = InventoryStatus.AwaitingConfirmation;
            inventory.ScanningFinishedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return new("Found", false, MapItem(item), "Скан сохранен.");
    }

    public async Task<InventoryDetailResponse> FinishScanningAsync(Guid id, Guid actorId, CancellationToken ct) =>
        (await FinishScanningAsync(id, actorId, true, ct))!;

    public async Task<InventoryDetailResponse?> FinishScanningAsync(Guid id, Guid actorId, bool includeDetails, CancellationToken ct, int? expectedRound = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var x = await LoadLockedAsync(id, ct, includeItems: false); EnsureAssignee(x, actorId, expectedRound);
        if (x.Status == InventoryStatus.AwaitingConfirmation) return includeDetails ? await GetByIdAsync(id, null, ct) : null;
        if (x.Status != InventoryStatus.Active) throw new ConflictException("Обход уже завершен или не запущен.");
        await db.InventoryItems.Where(i => i.InventoryId == id && i.Result == ScanResult.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(i => i.Result, ScanResult.NotFound), ct);
        x.Status = InventoryStatus.AwaitingConfirmation; x.ScanningFinishedAt = DateTimeOffset.UtcNow; AddAudit(actorId, "inventory.scanning-finished", x);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return includeDetails ? await GetByIdAsync(id, null, ct) : null;
    }

    public async Task<InventoryDetailResponse> ConfirmAsync(Guid id, ConfirmationRequest request, Guid actorId, CancellationToken ct) =>
        (await ConfirmAsync(id, request, actorId, true, ct))!;

    public async Task<InventoryDetailResponse?> ConfirmAsync(Guid id, ConfirmationRequest request, Guid actorId, bool includeDetails, CancellationToken ct, int? expectedRound = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var x = await LoadLockedAsync(id, ct, includeItems: false); var member = EnsureAssignee(x, actorId, expectedRound);
        if (x.Status == InventoryStatus.Completed && member.ConfirmationStatus == ConfirmationStatus.Confirmed)
        {
            await VerifyPasswordAsync(actorId, request.Password, ct);
            return includeDetails ? await GetByIdAsync(id, null, ct) : null;
        }
        if (x.Status != InventoryStatus.AwaitingConfirmation) throw new ConflictException("Проверка не ожидает подтверждения.");
        await VerifyPasswordAsync(actorId, request.Password, ct); member.ConfirmationStatus = ConfirmationStatus.Confirmed; member.ConfirmedAt = DateTimeOffset.UtcNow; member.RejectionReason = null;
        x.Status = InventoryStatus.Completed; x.CompletedAt = DateTimeOffset.UtcNow;
        AddAudit(actorId, "inventory.confirmed", x); await db.SaveChangesAsync(ct);
        await documents.GenerateInventoryReportsAsync(id, ct);
        await transaction.CommitAsync(ct);
        return includeDetails ? await GetByIdAsync(id, null, ct) : null;
    }

    public async Task<InventoryDetailResponse> RejectAsync(Guid id, RejectionRequest request, Guid actorId, CancellationToken ct) =>
        (await RejectAsync(id, request, actorId, true, ct))!;

    public async Task<InventoryDetailResponse?> RejectAsync(Guid id, RejectionRequest request, Guid actorId, bool includeDetails, CancellationToken ct, int? expectedRound = null)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) throw new RequestValidationException("Укажите причину отклонения.");
        if (reason.Length > 1000) throw new RequestValidationException("Причина отклонения не может быть длиннее 1000 символов.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var x = await LoadLockedAsync(id, ct, includeItems: false); var member = EnsureAssignee(x, actorId, expectedRound);
        if (x.Status == InventoryStatus.ReviewRequired && member.ConfirmationStatus == ConfirmationStatus.Rejected && member.RejectionReason == reason)
        {
            await VerifyPasswordAsync(actorId, request.Password, ct);
            return includeDetails ? await GetByIdAsync(id, null, ct) : null;
        }
        if (x.Status != InventoryStatus.AwaitingConfirmation) throw new ConflictException("Проверка не ожидает подтверждения.");
        await VerifyPasswordAsync(actorId, request.Password, ct); foreach (var m in x.Assignments) { m.ConfirmationStatus = ConfirmationStatus.Pending; m.ConfirmedAt = null; m.RejectionReason = null; }
        member.ConfirmationStatus = ConfirmationStatus.Rejected; member.RejectionReason = reason; x.Status = InventoryStatus.ReviewRequired;
        AddAudit(actorId, "inventory.rejected", x, new { request.Reason }); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return includeDetails ? await GetByIdAsync(id, null, ct) : null;
    }

    private static IQueryable<UserAccount> Candidates(AppDbContext db, Guid? targetEmployeeId) => db.Users.AsNoTracking()
        .Where(x => !x.IsBlocked && x.Role == UserRole.InventoryEmployee && x.Employee != null && x.Employee.IsEmployed && !x.Employee.IsArchived && !x.Employee.Department.IsArchived && x.Employee.Department.IsInventoryDepartment && (targetEmployeeId == null || x.EmployeeId != targetEmployeeId));

    internal static async Task AssignIfAvailableAsync(AppDbContext db, Inventory inventory, CancellationToken ct)
    {
        var candidates = await Candidates(db, inventory.TargetEmployeeId).Select(x => x.Id).ToListAsync(ct);
        if (candidates.Count > 0) inventory.Assignments.Add(new InventoryAssignment { UserId = candidates[Random.Shared.Next(candidates.Count)], Role = CommissionRole.Chairperson });
    }

    private async Task<Inventory> LoadLockedAsync(Guid id, CancellationToken ct, bool includeItems = true)
    {
        // Take the parent lock first, then read children in a fresh statement/snapshot.
        _ = await db.Inventories.FromSqlInterpolated($"SELECT * FROM inventories WHERE id = {id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Инвентаризация не найдена.");
        // EF identity resolution must not return values cached before the lock.
        foreach (var entry in db.ChangeTracker.Entries().Where(e =>
            e.Entity is Inventory i && i.Id == id ||
            e.Entity is InventoryAssetSnapshot item && item.InventoryId == id ||
            e.Entity is InventoryAssignment member && member.InventoryId == id).ToArray())
            entry.State = EntityState.Detached;
        return await LoadAsync(id, ct, includeItems);
    }

    private async Task<Inventory> LoadAsync(Guid id, CancellationToken ct, bool includeItems = true)
    {
        IQueryable<Inventory> query = db.Inventories;
        if (includeItems) query = query.Include(x => x.Items);
        return await query.Include(x => x.Assignments).ThenInclude(x => x.User).ThenInclude(x => x.Employee)
            .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Инвентаризация не найдена.");
    }
    private async Task<InventoryDetailResponse> MapAsync(Inventory x, CancellationToken ct)
    {
        var scans = db.UnexpectedScans.AsNoTracking().Where(s => s.InventoryId == x.Id);
        var unexpected = await (
            from scan in scans
            join asset in db.Assets.AsNoTracking() on scan.RecognizedAssetId equals asset.Id into recognizedAssets
            from asset in recognizedAssets.DefaultIfEmpty()
            orderby scan.ScannedAt
            select new UnexpectedScanResponse(scan.Id, scan.RawValue, scan.Type.ToString(), scan.RecognizedAssetId, asset == null ? null : asset.InventoryNumber, asset == null ? null : asset.Name, scan.ScannedAt)).ToListAsync(ct);
        var sourceNumber = x.SourceInventoryId is { } sourceId ? await db.Inventories.AsNoTracking().Where(i => i.Id == sourceId).Select(i => i.Number).SingleAsync(ct) : null;
        return new(x.Id, x.Number, x.Type.ToString(), x.Reason.ToString(), x.ReasonDetails, x.Status.ToString(), x.TargetEmployeeId, x.PlannedAt, x.StartedAt, x.ScanningFinishedAt, x.CompletedAt, x.Items.OrderBy(i => i.InventoryNumber).Select(MapItem).ToList(), x.Assignments.OrderBy(m => m.Role).Select(m => new InventoryAssigneeResponse(m.Id, m.UserId, m.User.Login, m.User.Employee?.FullName, m.Role.ToString(), m.ConfirmationStatus.ToString(), m.ConfirmedAt, m.RejectionReason)).ToList(), unexpected, x.SourceInventoryId, sourceNumber, Round: x.Round);
    }
    private static InventoryItemResponse MapItem(InventoryAssetSnapshot i) => new(i.Id, i.AssetId, i.InventoryNumber, i.Name, i.AccountingAccount, i.InitialCost, i.DepartmentName, i.LocationName, i.MateriallyResponsibleFullName, i.Result.ToString(), i.Condition?.ToString(), i.Comment, i.ScannedAt);
    private static InventoryAssignment EnsureAssignee(Inventory inventory, Guid userId, int? expectedRound)
    {
        var member = inventory.Assignments.SingleOrDefault(m => m.UserId == userId)
            ?? throw new UnauthorizedException("Пользователь не назначен на эту инвентаризацию.");
        if (expectedRound is int round && inventory.Round != round)
            throw new ConflictException("Обход был запущен заново. Вернитесь к проверке и откройте текущий обход.");
        return member;
    }
    private async Task VerifyPasswordAsync(Guid userId, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 256) throw new UnauthorizedException("Неверный пароль подтверждения.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct) ?? throw new UnauthorizedException();
        if (!await Security.UserAccess.IsActiveAsync(db, userId, ct) || string.IsNullOrEmpty(user.PasswordHash) || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
            throw new UnauthorizedException("Неверный пароль подтверждения.");
    }
    private async Task<ScanAssetResponse> SaveUnexpectedAsync(Inventory x, Guid actor, string raw, UnexpectedScanType type, Guid? assetId, CancellationToken ct)
    {
        var normalized = raw.Trim();
        // ScanAsync holds the inventory row lock, including during the duplicate check.
        var prior = db.UnexpectedScans.AsNoTracking().Where(scan => scan.InventoryId == x.Id && scan.Type == type);
        var duplicate = assetId is Guid recognized
            ? await prior.AnyAsync(scan => scan.RecognizedAssetId == recognized, ct)
            : await prior.AnyAsync(scan => scan.RawValue == normalized, ct);
        if (duplicate) return new(type.ToString(), true, null, "Этот QR уже отмечен как посторонний.");
        db.UnexpectedScans.Add(new UnexpectedScan { InventoryId = x.Id, ScannedByUserId = actor, RawValue = normalized, Type = type, RecognizedAssetId = assetId });
        AddAudit(actor, "inventory.unexpected-scan", x, new { raw, type, assetId });
        await db.SaveChangesAsync(ct);
        return new(type.ToString(), false, null, type == UnexpectedScanType.UnknownQr ? "QR не найден в реестре." : "ОС не входит в область проверки.");
    }
    private void AddAudit(Guid? actor, string action, Inventory x, object? data = null) => db.AuditLogs.Add(new AuditLog { UserId = actor, Action = action, EntityType = nameof(Inventory), EntityId = x.Id.ToString(), DataJson = data is null ? null : JsonSerializer.Serialize(data) });
    internal static string NewNumber()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return $"INV-{timestamp}-{RandomNumberGenerator.GetHexString(16)}";
    }
}
