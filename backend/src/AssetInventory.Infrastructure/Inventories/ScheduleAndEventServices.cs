using System.Text.Json;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Contracts.Common;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AssetInventory.Infrastructure.Inventories;

internal sealed class ScheduleService(AppDbContext db) : IScheduleService
{
    public Task<PagedResponse<ScheduleResponse>> GetPageAsync(PageQuery query, CancellationToken ct)
    {
        QueryPaging.Validate(query); var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        return db.InventorySchedules.AsNoTracking().Where(x => x.Name.ToLower().Contains(search)).OrderBy(x => x.NextRunAt).ThenBy(x => x.Id)
            .Select(x => new ScheduleResponse(x.Id, x.Name, x.IntervalMonths, x.NextRunAt, x.LaunchMode.ToString(), x.IsEnabled, x.LastRunAt, x.ConsecutiveFailures, x.RetryAt, x.DayOfMonth)).PageAsync(query, ct);
    }
    public async Task<IReadOnlyList<ScheduleResponse>> GetAsync(CancellationToken ct) => await db.InventorySchedules.AsNoTracking().OrderBy(x => x.NextRunAt).Select(x => Map(x)).ToListAsync(ct);
    public async Task<ScheduleResponse> GetByIdAsync(Guid id, CancellationToken ct) =>
        Map(await db.InventorySchedules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Расписание не найдено."));

    public async Task<ScheduleResponse> CreateAsync(UpsertScheduleRequest request, CancellationToken ct)
    {
        var local = Validate(request);
        var schedule = new InventorySchedule
        {
            Name = request.Name.Trim(), IntervalMonths = request.IntervalMonths, NextRunAt = local.ToUniversalTime(),
            DayOfMonth = local.Day, LaunchMode = ParseMode(request.LaunchMode), IsEnabled = request.IsEnabled
        };
        db.Add(schedule);
        await db.SaveChangesAsync(ct);
        return Map(schedule);
    }
    public async Task<ScheduleResponse> UpdateAsync(Guid id, UpsertScheduleRequest request, CancellationToken ct)
    {
        var local = Validate(request);
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var schedule = await db.InventorySchedules.FromSqlInterpolated($"SELECT * FROM inventory_schedules WHERE id = {id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Расписание не найдено.");
        var tracked = db.InventorySchedules.Local.SingleOrDefault(x => x.Id == id);
        if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
        db.InventorySchedules.Attach(schedule);
        schedule.Name = request.Name.Trim();
        schedule.IntervalMonths = request.IntervalMonths;
        if (schedule.DayOfMonth is null || ScheduleCalendar.Local(schedule.NextRunAt).Date != local.Date)
            schedule.DayOfMonth = local.Day;
        schedule.NextRunAt = local.ToUniversalTime();
        schedule.LaunchMode = ParseMode(request.LaunchMode);
        schedule.IsEnabled = request.IsEnabled;
        schedule.ConsecutiveFailures = 0;
        schedule.RetryAt = null;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(schedule);
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct) { var x = await db.InventorySchedules.FindAsync([id], ct) ?? throw new NotFoundException("Расписание не найдено."); db.Remove(x); await db.SaveChangesAsync(ct); }
    private static int ValidateMonths(int value) => value is >= 1 and <= 1200 ? value : throw new RequestValidationException("Период должен быть от 1 до 1200 месяцев.");
    private static DateTimeOffset Validate(UpsertScheduleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200) throw new RequestValidationException("Название расписания обязательно: не более 200 символов.");
        ValidateMonths(request.IntervalMonths); ParseMode(request.LaunchMode);
        if (request.NextRunAt == default) throw new RequestValidationException("Укажите дату следующего запуска.");
        DateTimeOffset local;
        try { local = ScheduleCalendar.Local(request.NextRunAt); }
        catch (ArgumentException) { throw new RequestValidationException("Дата следующего периода выходит за допустимый диапазон."); }
        if (ScheduleCalendar.Next(local, request.IntervalMonths, local.Day) is null)
            throw new RequestValidationException("Дата следующего периода выходит за допустимый диапазон.");
        return local;
    }
    private static ScheduleLaunchMode ParseMode(string value) => Enum.TryParse<ScheduleLaunchMode>(value, true, out var mode) && Enum.IsDefined(mode) ? mode : throw new RequestValidationException("Некорректный режим запуска.");
    private static ScheduleResponse Map(InventorySchedule x) => new(x.Id, x.Name, x.IntervalMonths, x.NextRunAt, x.LaunchMode.ToString(), x.IsEnabled, x.LastRunAt, x.ConsecutiveFailures, x.RetryAt, x.DayOfMonth);
}

internal sealed class IntegrationEventService(AppDbContext db) : IIntegrationEventService
{
    public Task<PagedResponse<IntegrationEventResponse>> GetPageAsync(PageQuery query, CancellationToken ct)
    {
        QueryPaging.Validate(query); var search = query.Search?.Trim().ToLowerInvariant() ?? "";
        return db.IntegrationEvents.AsNoTracking().Where(x => x.ExternalEventId.ToLower().Contains(search)).OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Id)
            .Select(x => new IntegrationEventResponse(x.Id, x.ExternalEventId, x.Type.ToString(), x.Status.ToString(), x.Attempts, x.LastError, x.CreatedInventoryId,
                db.Inventories.Where(i => i.Id == x.CreatedInventoryId).Select(i => i.Number).FirstOrDefault(), x.ReceivedAt, x.NextAttemptAt, x.ResultMessage)).PageAsync(query, ct);
    }
    public Task<IntegrationEventResponse> RegisterMolChangeAsync(MolChangeEventRequest request, CancellationToken ct) => RegisterAsync(request.EventId, request.EmployeeId, request.OccurredAt, InventoryReason.MolChange, request.Details, ct);
    public Task<IntegrationEventResponse> RegisterDismissalAsync(DismissalEventRequest request, CancellationToken ct) => RegisterAsync(request.EventId, request.EmployeeId, request.OccurredAt, InventoryReason.MolDismissal, null, ct);
    public async Task<IReadOnlyList<IntegrationEventResponse>> GetAsync(CancellationToken ct) => await db.IntegrationEvents.AsNoTracking().OrderByDescending(x => x.ReceivedAt).Take(500)
        .Select(x => new IntegrationEventResponse(x.Id, x.ExternalEventId, x.Type.ToString(), x.Status.ToString(), x.Attempts, x.LastError, x.CreatedInventoryId,
            db.Inventories.Where(i => i.Id == x.CreatedInventoryId).Select(i => i.Number).FirstOrDefault(), x.ReceivedAt, x.NextAttemptAt, x.ResultMessage)).ToListAsync(ct);
    public async Task<IntegrationEventResponse> RetryAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var e = await db.IntegrationEvents.FromSqlInterpolated($"SELECT * FROM integration_events WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Кадровое событие не найдено.");
        if (e.Status != IntegrationEventStatus.Failed || e.CreatedInventoryId is not null)
            throw new ConflictException("Повторить можно только событие с ошибкой, для которого ещё не создана инвентаризация.");
        db.AuditLogs.Add(new AuditLog { UserId = actorId, Action = "event.retry-requested", EntityType = nameof(IntegrationEvent), EntityId = e.Id.ToString(), DataJson = JsonSerializer.Serialize(new { eventId = e.ExternalEventId, previousAttempts = e.Attempts }) });
        e.Status = IntegrationEventStatus.Accepted;
        e.Attempts = 0;
        e.NextAttemptAt = null;
        e.ProcessedAt = null;
        e.LastError = null;
        e.ResultMessage = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Map(e);
    }
    private async Task<IntegrationEventResponse> RegisterAsync(string eventId, Guid employeeId, DateTimeOffset occurredAt, InventoryReason reason, string? details, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Trim().Length > 200) throw new RequestValidationException("eventId обязателен и не может быть длиннее 200 символов.");
        if (employeeId == Guid.Empty || occurredAt == default) throw new RequestValidationException("Укажите сотрудника и дату события.");
        if (details?.Trim().Length > 1000) throw new RequestValidationException("Описание события не может быть длиннее 1000 символов.");
        var normalizedId = eventId.Trim();
        var payload = new EventPayload(employeeId, occurredAt, reason, CleanDetails(details));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize only submissions sharing an event ID, including across API
        // replicas. The unique index remains the final database safeguard.
        var lockKey = "integration-event:" + normalizedId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        var existing = await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(x => x.ExternalEventId == normalizedId, ct);
        if (existing is not null)
        {
            if (!SameEventIdentity(existing.PayloadJson, payload))
                throw new ConflictException("Этот идентификатор уже относится к кадровому событию с другими данными.");
            var number = existing.CreatedInventoryId is { } inventoryId
                ? await db.Inventories.Where(i => i.Id == inventoryId).Select(i => i.Number).SingleOrDefaultAsync(ct)
                : null;
            await transaction.CommitAsync(ct);
            return Map(existing, number);
        }
        var body = JsonSerializer.Serialize(payload);
        var x = new IntegrationEvent { ExternalEventId = normalizedId, Type = reason == InventoryReason.MolChange ? IntegrationEventType.MolChanged : IntegrationEventType.EmployeeDismissed, PayloadJson = body, OccurredAt = occurredAt.ToUniversalTime() };
        db.Add(x); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Map(x);
    }
    private static string? CleanDetails(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool SameEventIdentity(string storedJson, EventPayload expected)
    {
        try
        {
            var original = JsonSerializer.Deserialize<EventPayload>(storedJson);
            return original is not null && original.EmployeeId == expected.EmployeeId
                && original.Reason == expected.Reason && original.OccurredAt == expected.OccurredAt;
        }
        catch (JsonException) { return false; }
    }
    private static IntegrationEventResponse Map(IntegrationEvent x, string? number = null) => new(x.Id, x.ExternalEventId, x.Type.ToString(), x.Status.ToString(), x.Attempts, x.LastError, x.CreatedInventoryId, number, x.ReceivedAt, x.NextAttemptAt, x.ResultMessage);
    internal sealed record EventPayload(Guid EmployeeId, DateTimeOffset OccurredAt, InventoryReason Reason, string? Details = null);
}

internal sealed class InventoryWorker(IServiceScopeFactory scopeFactory, ILogger<InventoryWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunQueueAsync("Кадровые события", static (processor, ct) => processor.ProcessEventsAsync(ct), stoppingToken),
        RunQueueAsync("Расписания", static (processor, ct) => processor.ProcessSchedulesAsync(ct), stoppingToken));

    private async Task RunQueueAsync(string queue, Func<IInventoryJobProcessor, CancellationToken, Task> process, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await process(scope.ServiceProvider.GetRequiredService<IInventoryJobProcessor>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ошибка фоновой обработки очереди {Queue}", queue); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}

internal sealed class InventoryJobProcessor(AppDbContext db, IInventoryService inventories, TimeProvider time, ILogger<InventoryJobProcessor> logger) : IInventoryJobProcessor
{
    public async Task ProcessEventsAsync(CancellationToken ct)
    {
        for (var index = 0; index < 20; index++)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = time.GetUtcNow();
            var e = await db.IntegrationEvents.FromSqlInterpolated($"SELECT * FROM integration_events WHERE status = 'Accepted' AND (next_attempt_at IS NULL OR next_attempt_at <= {now}) ORDER BY received_at FOR UPDATE SKIP LOCKED LIMIT 1").SingleOrDefaultAsync(ct);
            if (e is null) break;
            e.Status = IntegrationEventStatus.Processing; e.Attempts++;
            await db.SaveChangesAsync(ct);
            await transaction.CreateSavepointAsync("event_work", ct);
            try
            {
                await RegistryWriteGuard.BeginAsync(db, true, ct);
                var payload = JsonSerializer.Deserialize<IntegrationEventService.EventPayload>(e.PayloadJson) ?? throw new InvalidOperationException("Пустое событие.");
                var employee = await db.Employees.FindAsync([payload.EmployeeId], ct);
                var hasAssets = employee is not null && employee.IsMateriallyResponsible && await db.Assets.AnyAsync(a => a.MateriallyResponsibleEmployeeId == payload.EmployeeId && a.ArchivedAt == null && a.Status != AssetStatus.Archived, ct);
                e.ResultMessage = employee is null ? "Инвентаризация не создана: сотрудник не найден."
                    : !employee.IsMateriallyResponsible ? "Инвентаризация не создана: сотрудник не является МОЛ."
                    : "Инвентаризация не создана: за МОЛ нет активных ОС.";
                string? startupWarning = null;
                if (hasAssets)
                {
                    var inventory = new Inventory { Number = InventoryService.NewNumber(), Type = InventoryType.EventDriven, Reason = payload.Reason, ReasonDetails = $"Автоматически по событию {e.ExternalEventId}" + (string.IsNullOrWhiteSpace(payload.Details) ? "" : $"\n{payload.Details}"), TargetEmployeeId = payload.EmployeeId, PlannedAt = now };
                    await InventoryService.AssignIfAvailableAsync(db, inventory, ct);
                    db.Inventories.Add(inventory);
                    // Persist the event inventory inside the event transaction so
                    // StartAsync can take its normal lock and build the asset snapshot.
                    await db.SaveChangesAsync(ct);
                    e.CreatedInventoryId = inventory.Id;
                    try
                    {
                        await inventories.StartAsync(inventory.Id, Guid.Empty, ct);
                        e.ResultMessage = "Инвентаризация запущена.";
                    }
                    catch (ConflictException ex)
                    {
                        // Keep the event and inventory visible to administrators
                        // when automatic startup needs human attention.
                        db.Inventories.Local.Single(i => i.Id == inventory.Id).Status = InventoryStatus.NeedsAttention;
                        startupWarning = $"Проверка создана, но не запущена автоматически: {ex.Message}";
                        e.ResultMessage = startupWarning;
                        logger.LogWarning(ex, "Автозапуск событийной инвентаризации {InventoryId} требует внимания", inventory.Id);
                    }
                }
                e.Status = IntegrationEventStatus.Processed; e.ProcessedAt = now; e.LastError = startupWarning; e.NextAttemptAt = null;
                // Include database writes in the retry boundary, not just mapping.
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await transaction.RollbackToSavepointAsync("event_work", ct);
                var eventId = e.Id;
                db.ChangeTracker.Clear();
                e = await db.IntegrationEvents.SingleAsync(x => x.Id == eventId, ct);
                e.LastError = ex.Message;
                e.ResultMessage = null;
                e.Status = e.Attempts >= 5 ? IntegrationEventStatus.Failed : IntegrationEventStatus.Accepted;
                e.NextAttemptAt = e.Status == IntegrationEventStatus.Failed ? null : time.GetUtcNow().AddSeconds(15 * Math.Pow(2, e.Attempts - 1));
                e.ProcessedAt = e.Status == IntegrationEventStatus.Failed ? time.GetUtcNow() : null;
                e.CreatedInventoryId = null;
                await db.SaveChangesAsync(ct);
                logger.LogWarning(ex, "Ошибка события {EventId}, попытка {Attempt}, статус {Status}, следующий запуск {NextAttemptAt}", e.ExternalEventId, e.Attempts, e.Status, e.NextAttemptAt);
            }
            await transaction.CommitAsync(ct);
        }

        await StartLegacyEventDraftsAsync(ct);
    }

    private async Task StartLegacyEventDraftsAsync(CancellationToken ct)
    {
        // Events from older worker versions may already be processed while their inventory is still a draft.
        var failedDrafts = new List<Guid>();
        for (var index = 0; index < 20; index++)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Take the registry gate before locking the inventory, as StartAsync does.
            await RegistryWriteGuard.BeginAsync(db, true, ct);
            var now = time.GetUtcNow();
            var inventory = await db.Inventories.FromSqlInterpolated($"""
                SELECT i.* FROM inventories i
                INNER JOIN integration_events e ON e.created_inventory_id = i.id
                WHERE e.status = 'Processed' AND i.status = 'Draft'
                    AND (e.next_attempt_at IS NULL OR e.next_attempt_at <= {now})
                    AND NOT (i.id = ANY({failedDrafts.ToArray()}))
                ORDER BY GREATEST(i.created_at, COALESCE(e.next_attempt_at, i.created_at)), i.id
                FOR UPDATE OF i SKIP LOCKED LIMIT 1
                """).SingleOrDefaultAsync(ct);
            if (inventory is null) break;
            var inventoryId = inventory.Id;
            var e = await db.IntegrationEvents.SingleAsync(x => x.CreatedInventoryId == inventoryId, ct);
            await transaction.CreateSavepointAsync("legacy_event_start", ct);
            try
            {
                await inventories.StartAsync(inventoryId, Guid.Empty, ct);
                e.ResultMessage = "Инвентаризация запущена.";
                e.LastError = null;
                e.NextAttemptAt = null;
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failedDrafts.Add(inventoryId);
                logger.LogWarning(ex, "Не удалось запустить ранее созданную событийную инвентаризацию {InventoryId}", inventoryId);
                try
                {
                    var eventId = e.Id;
                    await transaction.RollbackToSavepointAsync("legacy_event_start", ct);
                    db.ChangeTracker.Clear();
                    e = await db.IntegrationEvents.SingleAsync(x => x.Id == eventId, ct);
                    if (ex is ConflictException)
                    {
                        var draft = await db.Inventories.SingleAsync(x => x.Id == inventoryId, ct);
                        draft.Status = InventoryStatus.NeedsAttention;
                        e.LastError = $"Проверка создана, но не запущена автоматически: {ex.Message}";
                        e.NextAttemptAt = null;
                    }
                    else
                    {
                        e.LastError = "Автозапуск проверки временно отложен. Система повторит попытку.";
                        e.NextAttemptAt = time.GetUtcNow().AddSeconds(30);
                    }
                    e.ResultMessage = e.LastError;
                    await db.SaveChangesAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception retryError)
                {
                    logger.LogError(retryError, "Не удалось записать результат автозапуска инвентаризации {InventoryId}", inventoryId);
                    continue;
                }
            }
            await transaction.CommitAsync(ct);
        }
    }

    public async Task ProcessSchedulesAsync(CancellationToken ct)
    {
        var failedSchedules = new List<Guid>();
        for (var index = 0; index < 20; index++)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = time.GetUtcNow();
            var s = await db.InventorySchedules.FromSqlInterpolated($"""
                SELECT * FROM inventory_schedules
                WHERE is_enabled = TRUE AND next_run_at <= {now}
                    AND (retry_at IS NULL OR retry_at <= {now}) AND NOT (id = ANY({failedSchedules.ToArray()}))
                ORDER BY GREATEST(next_run_at, COALESCE(retry_at, next_run_at)), id
                FOR UPDATE SKIP LOCKED LIMIT 1
                """).SingleOrDefaultAsync(ct);
            if (s is null) break;
            var id = s.Id;
            var plannedAt = s.NextRunAt;
            await transaction.CreateSavepointAsync("schedule_work", ct);
            try
            {
                await RunScheduleAsync(s, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failedSchedules.Add(id);
                logger.LogError(ex, "Ошибка расписания {ScheduleId} за период {PlannedAt}", id, plannedAt);
                try
                {
                    await transaction.RollbackToSavepointAsync("schedule_work", ct);
                    db.ChangeTracker.Clear();
                    var failed = await db.InventorySchedules.SingleAsync(x => x.Id == id, ct);
                    failed.ConsecutiveFailures = Math.Min(failed.ConsecutiveFailures, int.MaxValue - 1) + 1;
                    var delay = Math.Min(300, 15 * Math.Pow(2, Math.Min(failed.ConsecutiveFailures - 1, 5)));
                    failed.RetryAt = time.GetUtcNow().AddSeconds(delay);
                    await db.SaveChangesAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception retryError)
                {
                    logger.LogError(retryError, "Не удалось сохранить время повтора расписания {ScheduleId}", id);
                    continue;
                }
            }
            await transaction.CommitAsync(ct);
        }
    }

    private async Task RunScheduleAsync(InventorySchedule schedule, DateTimeOffset now, CancellationToken ct)
    {
        var period = new DateOnly(schedule.NextRunAt.Year, schedule.NextRunAt.Month, 1);
        var marker = $"Расписание:{schedule.Id}:{schedule.NextRunAt.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";
        var inventory = await db.Inventories.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(x =>
            x.SourceScheduleId == schedule.Id && x.ScheduledPeriod == period || x.ReasonDetails == marker, ct);
        if (inventory is null)
        {
            await RegistryWriteGuard.BeginAsync(db, true, ct);
            inventory = new Inventory { Number = InventoryService.NewNumber(), Type = InventoryType.Planned, Reason = InventoryReason.AnnualReporting, ReasonDetails = marker, PlannedAt = schedule.NextRunAt, Status = InventoryStatus.Draft, SourceScheduleId = schedule.Id, ScheduledPeriod = period };
            await InventoryService.AssignIfAvailableAsync(db, inventory, ct);
            db.Inventories.Add(inventory); await db.SaveChangesAsync(ct);
        }
        else if (inventory.SourceScheduleId is null)
        {
            // Preserve legacy drafts created before structured period keys.
            inventory.SourceScheduleId = schedule.Id; inventory.ScheduledPeriod = period;
            await db.SaveChangesAsync(ct);
        }
        if (schedule.LaunchMode == ScheduleLaunchMode.AutoStart && inventory.Status is InventoryStatus.Draft or InventoryStatus.NeedsAttention)
        {
            try { await inventories.StartAsync(inventory.Id, Guid.Empty, ct); }
            catch (ConflictException ex)
            {
                var current = db.Inventories.Local.Single(x => x.Id == inventory.Id);
                current.Status = InventoryStatus.NeedsAttention;
                logger.LogWarning(ex, "Автозапуск проверки {InventoryId} требует внимания", inventory.Id);
            }
        }
        schedule.LastRunAt = now;
        // Existing rows derive the recurring day from the first preserved date.
        schedule.DayOfMonth ??= ScheduleCalendar.Local(schedule.NextRunAt).Day;
        if (ScheduleCalendar.Next(schedule.NextRunAt, schedule.IntervalMonths, schedule.DayOfMonth.Value) is { } next)
            schedule.NextRunAt = next;
        else schedule.IsEnabled = false;
        schedule.ConsecutiveFailures = 0;
        schedule.RetryAt = null;
        await db.SaveChangesAsync(ct);
    }
}
