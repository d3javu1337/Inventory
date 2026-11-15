using System.Text.Json;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class WorkerReliabilityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ScheduleWorkerRunsWhileEventQueueIsBusy()
    {
        await using var s = await fixture.OpenAsync();
        var probe = new QueueProbe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(s.Configuration);
        services.RemoveAll<IInventoryJobProcessor>();
        services.AddSingleton<IInventoryJobProcessor>(probe);
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single(service => service.GetType().Name == "InventoryWorker");
        await worker.StartAsync(Ct);
        try
        {
            await probe.EventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await probe.SchedulesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class QueueProbe : IInventoryJobProcessor
    {
        public TaskCompletionSource EventsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SchedulesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProcessEventsAsync(CancellationToken ct)
        {
            EventsStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        public Task ProcessSchedulesAsync(CancellationToken ct)
        {
            SchedulesStarted.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task InvalidSchedulesCannotPoisonTheBackgroundQueue()
    {
        await using var s = await fixture.OpenAsync();
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        var valid = new UpsertScheduleRequest("Проверка", 12, DateTimeOffset.UtcNow.AddDays(1), "CreateDraft", true);
        foreach (var invalid in new[] { valid with { Name = null! }, valid with { Name = " " }, valid with { Name = new string('N', 201) }, valid with { NextRunAt = default }, valid with { NextRunAt = DateTimeOffset.MaxValue } })
            await Assert.ThrowsAsync<RequestValidationException>(() => schedules.CreateAsync(invalid, Ct));
        var created = await schedules.CreateAsync(valid, Ct);
        await Assert.ThrowsAsync<RequestValidationException>(() => schedules.UpdateAsync(created.Id, valid with { IntervalMonths = 0 }, Ct));
        Assert.Equal("Проверка", (await schedules.GetAsync(Ct)).Single().Name);
        Assert.Equal(12, (await schedules.GetAsync(Ct)).Single().IntervalMonths);
    }

    [Fact]
    public async Task ScheduleAcceptsAnOffsetAndUsesTheUtcCalendarPeriod()
    {
        var local = new DateTimeOffset(2026, 11, 1, 0, 15, 0, TimeSpan.FromHours(5));
        var utc = local.ToUniversalTime();
        await using var s = await fixture.OpenAsync(new TestClock(utc.AddMinutes(1)));
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        var request = new UpsertScheduleRequest("Проверка", 1, local.AddMonths(1), "CreateDraft", true);
        var created = await schedules.CreateAsync(request, Ct);
        Assert.True(created.NextRunAt.EqualsExact(request.NextRunAt.ToUniversalTime()));
        var updated = await schedules.UpdateAsync(created.Id, request with { NextRunAt = local }, Ct);
        Assert.True(updated.NextRunAt.EqualsExact(utc));
        await SchedulesAsync(s);

        var inventory = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(new DateOnly(2026, 10, 1), inventory.ScheduledPeriod);
        Assert.True(inventory.PlannedAt.EqualsExact(utc));
        Assert.Equal(utc.AddMonths(1), (await schedules.GetAsync(Ct)).Single().NextRunAt);
    }

    [Theory]
    [InlineData(2026, 28, 1)]
    [InlineData(2028, 29, 12)]
    public async Task MonthlyScheduleKeepsLocalTimeAndReturnsToItsAnchorDay(int year, int februaryDay, int hour)
    {
        var offset = TimeSpan.FromHours(5);
        var january = new DateTimeOffset(year, 1, 31, hour, 15, 0, offset);
        var february = new DateTimeOffset(year, 2, februaryDay, hour, 15, 0, offset);
        var march = new DateTimeOffset(year, 3, 31, hour, 15, 0, offset);
        var clock = new TestClock(january.ToUniversalTime().AddMinutes(1));
        await using var s = await fixture.OpenAsync(clock);
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        var request = new UpsertScheduleRequest("Месячная проверка", 1, january, "CreateDraft", true);
        var created = await schedules.CreateAsync(request, Ct);
        await SchedulesAsync(s);

        var afterJanuary = (await schedules.GetAsync(Ct)).Single();
        Assert.Equal(february.ToUniversalTime(), afterJanuary.NextRunAt);
        Assert.Equal(31, afterJanuary.DayOfMonth);
        await schedules.UpdateAsync(created.Id, request with { Name = "Уточненное название", NextRunAt = afterJanuary.NextRunAt }, Ct);
        clock.Set(february.ToUniversalTime().AddMinutes(1));
        await SchedulesAsync(s);
        Assert.Equal(march.ToUniversalTime(), (await schedules.GetAsync(Ct)).Single().NextRunAt);
        Assert.Equal(new[] { january.ToUniversalTime(), february.ToUniversalTime() },
            await s.Db.Inventories.AsNoTracking().OrderBy(i => i.PlannedAt).Select(i => i.PlannedAt).ToArrayAsync(Ct));
    }

    [Fact]
    public async Task CalendarMigrationPreservesExistingNextRunAndUsesItsLocalDayForLaterPeriods()
    {
        var planned = new DateTimeOffset(2026, 1, 31, 1, 15, 0, TimeSpan.FromHours(5)).ToUniversalTime();
        var clock = new TestClock(planned.AddMinutes(1));
        await using var s = await fixture.OpenAsync(clock);
        var id = Guid.CreateVersion7();
        var migrator = s.Db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261107174857_InventoryRound", Ct);
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory_schedules ("Id", "Name", "IntervalMonths", "NextRunAt", "LaunchMode", "IsEnabled")
            VALUES ({id}, 'Прежнее расписание', 1, {planned}, 'CreateDraft', TRUE)
            """, Ct);
        await migrator.MigrateAsync(cancellationToken: Ct);

        var stored = await s.Db.InventorySchedules.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(planned, stored.NextRunAt);
        Assert.Null(stored.DayOfMonth);
        await SchedulesAsync(s);
        var updated = await s.Db.InventorySchedules.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(31, updated.DayOfMonth);
        Assert.Equal(new DateTimeOffset(2026, 2, 27, 20, 15, 0, TimeSpan.Zero), updated.NextRunAt);
        Assert.Equal(planned, (await s.Db.Inventories.AsNoTracking().SingleAsync(Ct)).PlannedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HrEventAcceptsAnOffsetAndKeepsTheSourceTimeInItsPayload(bool dismissal)
    {
        await using var s = await fixture.OpenAsync();
        var mol = await SeedAssetAndWorkerAsync(s);
        var local = new DateTimeOffset(2026, 11, 1, 0, 15, 0, TimeSpan.FromHours(5));
        var events = s.Services.GetRequiredService<IIntegrationEventService>();
        if (dismissal) await events.RegisterDismissalAsync(new("offset", mol, local), Ct);
        else await events.RegisterMolChangeAsync(new("offset", mol, local, null), Ct);
        var received = await ReadEventAsync(s, "offset");
        Assert.True(received.OccurredAt.EqualsExact(local.ToUniversalTime()));
        using var payload = JsonDocument.Parse(received.PayloadJson);
        Assert.True(payload.RootElement.GetProperty("OccurredAt").GetDateTimeOffset().EqualsExact(local));
        await EventsAsync(s);
        Assert.Equal(InventoryStatus.Active, (await s.Db.Inventories.AsNoTracking().SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task WorkerMigrationPreservesExistingInventoriesAndPendingEvents()
    {
        await using var s = await fixture.OpenAsync();
        var migrator = s.Db.GetService<IMigrator>();
        // Downgrade only the disposable fixture database to simulate an upgrade
        // from the previous released schema with existing rows.
        await migrator.MigrateAsync("20260917031535_SingleInventoryAssignee", Ct);
        var inventoryId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventories (\"Id\", \"Number\", \"Type\", \"Reason\", \"Status\", \"PlannedAt\", \"CreatedAt\") VALUES ({inventoryId}, 'OLD-INVENTORY', 'Planned', 'AnnualReporting', 'Draft', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)", Ct);
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO integration_events (\"Id\", \"ExternalEventId\", \"Type\", \"PayloadJson\", \"Status\", \"Attempts\", \"OccurredAt\", \"ReceivedAt\") VALUES ({eventId}, 'OLD-EVENT', 'EmployeeDismissed', '{{}}'::jsonb, 'Pending', 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)", Ct);
        await migrator.MigrateAsync(cancellationToken: Ct);
        var inventory = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(inventoryId, inventory.Id);
        Assert.Equal("OLD-INVENTORY", inventory.Number);
        Assert.Null(inventory.SourceScheduleId);
        Assert.Null(inventory.ScheduledPeriod);
        var e = await s.Db.IntegrationEvents.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(eventId, e.Id);
        Assert.Equal(IntegrationEventStatus.Accepted, e.Status);
        Assert.Null(e.NextAttemptAt);
    }

    [Fact]
    public async Task ConcurrentSubmissionsReturnOneEventAndWorkersStartOneInventory()
    {
        await using var s = await fixture.OpenAsync();
        var mol = await SeedAssetAndWorkerAsync(s);
        var occurred = DateTimeOffset.UtcNow;
        async Task<IntegrationEventResponse> RegisterAsync()
        {
            await using var scope = s.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IIntegrationEventService>()
                .RegisterDismissalAsync(new(" same-event ", mol, occurred), Ct);
        }
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RegisterAsync()));
        Assert.Single(responses.Select(x => x.Id).Distinct());
        Assert.Equal(1, await s.Db.IntegrationEvents.AsNoTracking().CountAsync(Ct));
        await Task.WhenAll(EventsAsync(s), EventsAsync(s));
        var e = await s.Db.IntegrationEvents.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(IntegrationEventStatus.Processed, e.Status);
        Assert.Equal(1, e.Attempts);
        var inventory = await s.Db.Inventories.AsNoTracking().Include(x => x.Assignments).SingleAsync(Ct);
        Assert.Equal(inventory.Id, e.CreatedInventoryId);
        Assert.Equal(mol, inventory.TargetEmployeeId);
        Assert.Equal(InventoryStatus.Active, inventory.Status);
        Assert.Single(inventory.Assignments);
        Assert.Single(await s.Db.InventoryItems.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("Инвентаризация запущена.", e.ResultMessage);
        var repeated = await RegisterAsync();
        Assert.Equal(e.Id, repeated.Id);
        Assert.Equal("Processed", repeated.Status);
    }

    [Fact]
    public async Task DuplicateHrEventIdAcceptsAnEquivalentRetryAndRejectsDifferentData()
    {
        await using var s = await fixture.OpenAsync();
        var mol = await SeedAssetAndWorkerAsync(s);
        var events = s.Services.GetRequiredService<IIntegrationEventService>();
        var occurredAt = new DateTimeOffset(2026, 11, 8, 17, 0, 0, TimeSpan.FromHours(5));
        var request = new MolChangeEventRequest("duplicate-hr", mol, occurredAt, " Передача ");
        var first = await events.RegisterMolChangeAsync(request, Ct);
        foreach (var conflicting in new[]
        {
            request with { EmployeeId = Guid.CreateVersion7() },
            request with { OccurredAt = occurredAt.AddSeconds(1) }
        })
            await Assert.ThrowsAsync<ConflictException>(() => events.RegisterMolChangeAsync(conflicting, Ct));
        Assert.Equal(first.Id, (await events.RegisterMolChangeAsync(request with { Details = "Другое описание" }, Ct)).Id);
        await Assert.ThrowsAsync<ConflictException>(() => events.RegisterDismissalAsync(new(request.EventId, mol, occurredAt), Ct));
        await EventsAsync(s);
        var repeat = await events.RegisterMolChangeAsync(request with { OccurredAt = occurredAt.ToUniversalTime(), Details = "Передача" }, Ct);
        Assert.Equal(first.Id, repeat.Id);
        Assert.Equal("Processed", repeat.Status);
        var inventory = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(inventory.Id, repeat.CreatedInventoryId);
        Assert.Equal(inventory.Number, repeat.CreatedInventoryNumber);
        Assert.Equal(mol, inventory.TargetEmployeeId);
        Assert.Contains("Передача", inventory.ReasonDetails!);
        Assert.DoesNotContain("Другое описание", inventory.ReasonDetails!);
        Assert.Equal(1, await s.Db.IntegrationEvents.AsNoTracking().CountAsync(Ct));
    }

    [Fact]
    public async Task MolChangeIsDistinctAndEventsWithoutAssetsCompleteWithoutInventory()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Отдел", false), Ct);
        var nonMol = await s.Catalog.CreateEmployeeAsync(new("E", "Сотрудник", dep.Id, false, true), Ct);
        var emptyMol = await s.Catalog.CreateEmployeeAsync(new("M", "МОЛ без имущества", dep.Id, true, true), Ct);
        var events = s.Services.GetRequiredService<IIntegrationEventService>();
        var change = await events.RegisterMolChangeAsync(new("change", emptyMol.Id, DateTimeOffset.UtcNow, null), Ct);
        Assert.Equal("MolChanged", change.Type);
        await events.RegisterDismissalAsync(new("non-mol", nonMol.Id, DateTimeOffset.UtcNow), Ct);
        await events.RegisterDismissalAsync(new("missing", Guid.NewGuid(), DateTimeOffset.UtcNow), Ct);
        await EventsAsync(s);
        Assert.True(await s.Db.IntegrationEvents.AsNoTracking().AllAsync(x => x.Status == IntegrationEventStatus.Processed && x.CreatedInventoryId == null && x.Attempts == 1, Ct));
        Assert.Empty(await s.Db.Inventories.AsNoTracking().ToListAsync(Ct));
        Assert.Contains("не является МОЛ", (await ReadEventAsync(s, "non-mol")).ResultMessage);
        Assert.Contains("нет активных ОС", (await ReadEventAsync(s, "change")).ResultMessage);
        Assert.Contains("не найден", (await ReadEventAsync(s, "missing")).ResultMessage);
        await Assert.ThrowsAsync<RequestValidationException>(() => events.RegisterDismissalAsync(new(new string('x', 201), nonMol.Id, DateTimeOffset.UtcNow), Ct));
    }

    [Fact]
    public async Task RetriesUsePersistedExponentialDelaysAndStopAfterFiveAttempts()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var s = await fixture.OpenAsync(clock);
        s.Db.IntegrationEvents.Add(new IntegrationEvent { ExternalEventId = "broken", Type = IntegrationEventType.EmployeeDismissed, PayloadJson = "[]", OccurredAt = clock.GetUtcNow() });
        await s.Db.SaveChangesAsync(Ct);
        await EventsAsync(s);
        var e = await ReadEventAsync(s, "broken");
        Assert.Equal(1, e.Attempts);
        Assert.Equal(clock.GetUtcNow().AddSeconds(15), e.NextAttemptAt);
        Assert.NotNull(e.LastError);
        await EventsAsync(s);
        Assert.Equal(1, (await ReadEventAsync(s, "broken")).Attempts);
        clock.Set(e.NextAttemptAt!.Value.AddTicks(-1));
        await EventsAsync(s);
        Assert.Equal(1, (await ReadEventAsync(s, "broken")).Attempts);
        for (var attempt = 2; attempt <= 5; attempt++)
        {
            clock.Set(e.NextAttemptAt!.Value);
            await EventsAsync(s);
            e = await ReadEventAsync(s, "broken");
            Assert.Equal(attempt, e.Attempts);
            if (attempt < 5)
            {
                Assert.Equal(IntegrationEventStatus.Accepted, e.Status);
                Assert.Equal(clock.GetUtcNow().AddSeconds(15 * Math.Pow(2, attempt - 1)), e.NextAttemptAt);
            }
        }
        Assert.Equal(IntegrationEventStatus.Failed, e.Status);
        Assert.Null(e.NextAttemptAt);
        Assert.NotNull(e.ProcessedAt);
        clock.Set(clock.GetUtcNow().AddDays(1));
        await EventsAsync(s);
        Assert.Equal(5, (await ReadEventAsync(s, "broken")).Attempts);
        Assert.Empty(await s.Db.Inventories.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task DatabaseFailureIsRetriedWithoutOrphansOrBlockingHealthyEvents()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var s = await fixture.OpenAsync(clock);
        var mol = await SeedAssetAndWorkerAsync(s);
        var events = s.Services.GetRequiredService<IIntegrationEventService>();
        await events.RegisterDismissalAsync(new("persist-failure", mol, clock.GetUtcNow()), Ct);
        await events.RegisterDismissalAsync(new("healthy", mol, clock.GetUtcNow()), Ct);
        // Inject a persistence failure only into this disposable test database.
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_event_inventory() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.reason_details = 'Автоматически по событию persist-failure' THEN
                    RAISE EXCEPTION 'temporary inventory write failure';
                END IF;
                RETURN NEW;
            END; $$;
            CREATE TRIGGER fail_event_inventory BEFORE INSERT ON inventories
            FOR EACH ROW EXECUTE FUNCTION fail_event_inventory();
            """, Ct);
        await EventsAsync(s);
        var failed = await ReadEventAsync(s, "persist-failure");
        Assert.Equal(IntegrationEventStatus.Accepted, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.Null(failed.CreatedInventoryId);
        Assert.NotNull(failed.NextAttemptAt);
        Assert.NotNull(failed.LastError);
        Assert.Equal(IntegrationEventStatus.Processed, (await ReadEventAsync(s, "healthy")).Status);
        Assert.Equal(1, await s.Db.Inventories.AsNoTracking().CountAsync(Ct));
        Assert.Equal(1, await s.Db.InventoryAssignments.AsNoTracking().CountAsync(Ct));
        await s.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_event_inventory ON inventories; DROP FUNCTION fail_event_inventory();", Ct);
        clock.Set(failed.NextAttemptAt!.Value);
        await EventsAsync(s);
        var recovered = await ReadEventAsync(s, "persist-failure");
        Assert.Equal(IntegrationEventStatus.Processed, recovered.Status);
        Assert.Equal(2, recovered.Attempts);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.NextAttemptAt);
        Assert.Equal(2, await s.Db.Inventories.AsNoTracking().CountAsync(Ct));
        Assert.Equal(2, await s.Db.InventoryAssignments.AsNoTracking().CountAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentSchedulesAndReplayingThePeriodCreateOnlyOneInventory()
    {
        await using var s = await fixture.OpenAsync();
        var scheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        var schedule = await schedules.CreateAsync(new("Годовая", 12, scheduledAt, "CreateDraft", true), Ct);
        await Task.WhenAll(SchedulesAsync(s), SchedulesAsync(s));
        var inventory = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(schedule.Id, inventory.SourceScheduleId);
        Assert.Equal(new DateOnly(scheduledAt.Year, scheduledAt.Month, 1), inventory.ScheduledPeriod);
        var saved = await s.Db.InventorySchedules.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(scheduledAt.AddMonths(12), saved.NextRunAt, TimeSpan.FromMilliseconds(1));
        await s.Db.InventorySchedules.Where(x => x.Id == schedule.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.NextRunAt, scheduledAt), Ct);
        await SchedulesAsync(s);
        Assert.Equal(1, await s.Db.Inventories.AsNoTracking().CountAsync(Ct));
        s.Db.Inventories.Add(new Inventory { Number = "DUPLICATE", Type = InventoryType.Planned, Reason = InventoryReason.AnnualReporting, PlannedAt = scheduledAt, SourceScheduleId = schedule.Id, ScheduledPeriod = inventory.ScheduledPeriod });
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ScheduleFailureRollsBackItsPeriodAndDoesNotBlockOtherRules()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var s = await fixture.OpenAsync(clock);
        await SeedAssetAndWorkerAsync(s);
        var scheduledAt = clock.GetUtcNow().AddMinutes(-1);
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        var schedule = await schedules.CreateAsync(new("Автозапуск", 12, scheduledAt, "AutoStart", true), Ct);
        var healthy = await schedules.CreateAsync(new("Черновик", 12, scheduledAt.AddSeconds(30), "CreateDraft", true), Ct);
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_schedule_advance() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.name = 'Автозапуск' AND NEW.last_run_at IS NOT NULL THEN
                    RAISE EXCEPTION 'temporary schedule write failure';
                END IF;
                RETURN NEW;
            END; $$;
            CREATE TRIGGER fail_schedule_advance BEFORE UPDATE ON inventory_schedules
            FOR EACH ROW EXECUTE FUNCTION fail_schedule_advance();
            """, Ct);
        await SchedulesAsync(s);
        var draft = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(healthy.Id, draft.SourceScheduleId);
        Assert.Equal(InventoryStatus.Draft, draft.Status);
        Assert.Empty(await s.Db.InventoryItems.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(draft.Id, (await s.Db.InventoryAssignments.AsNoTracking().SingleAsync(Ct)).InventoryId);
        Assert.Equal(0, await s.Db.AuditLogs.CountAsync(x => x.Action == "inventory.started", Ct));
        var saved = await s.Db.InventorySchedules.AsNoTracking().SingleAsync(x => x.Id == schedule.Id, Ct);
        Assert.Equal(scheduledAt, saved.NextRunAt, TimeSpan.FromMilliseconds(1));
        Assert.Null(saved.LastRunAt);
        Assert.Equal(1, saved.ConsecutiveFailures);
        Assert.Equal(clock.GetUtcNow().AddSeconds(15), saved.RetryAt);
        Assert.NotNull((await s.Db.InventorySchedules.AsNoTracking().SingleAsync(x => x.Id == healthy.Id, Ct)).LastRunAt);
        await s.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_schedule_advance ON inventory_schedules; DROP FUNCTION fail_schedule_advance();", Ct);
        await SchedulesAsync(s);
        Assert.Equal(1, await s.Db.Inventories.CountAsync(Ct));
        clock.Set(saved.RetryAt!.Value);
        await SchedulesAsync(s);
        var inventory = await s.Db.Inventories.AsNoTracking().Include(x => x.Items).Include(x => x.Assignments).SingleAsync(x => x.SourceScheduleId == schedule.Id, Ct);
        Assert.Equal(InventoryStatus.Active, inventory.Status);
        Assert.Single(inventory.Items);
        Assert.Single(inventory.Assignments);
        Assert.Equal(2, await s.Db.Inventories.CountAsync(Ct));
        var recovered = await s.Db.InventorySchedules.AsNoTracking().SingleAsync(x => x.Id == schedule.Id, Ct);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.RetryAt);
    }

    [Fact]
    public async Task FailedScheduleBatchDoesNotStarveLaterRuleOnNextPass()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var s = await fixture.OpenAsync(clock);
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        var due = clock.GetUtcNow().AddMinutes(-2);
        for (var index = 0; index < 20; index++)
            await schedules.CreateAsync(new($"Сбой {index:00}", 12, due, "CreateDraft", true), Ct);
        var healthy = await schedules.CreateAsync(new("Исправное", 12, due.AddMinutes(1), "CreateDraft", true), Ct);
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_earliest_schedules() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.name LIKE 'Сбой %' AND NEW.last_run_at IS NOT NULL THEN
                    RAISE EXCEPTION 'test schedule advancement failure';
                END IF;
                RETURN NEW;
            END; $$;
            CREATE TRIGGER fail_earliest_schedules BEFORE UPDATE ON inventory_schedules
            FOR EACH ROW EXECUTE FUNCTION fail_earliest_schedules();
            """, Ct);

        await SchedulesAsync(s);
        Assert.Empty(await s.Db.Inventories.AsNoTracking().ToListAsync(Ct));
        var retryAt = clock.GetUtcNow().AddSeconds(15);
        Assert.Equal(20, await s.Db.InventorySchedules.CountAsync(x => x.ConsecutiveFailures == 1 && x.RetryAt == retryAt, Ct));
        // The failed rules are eligible again, but their retry comes after the waiting healthy rule.
        clock.Set(retryAt);
        await SchedulesAsync(s);
        var inventory = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(healthy.Id, inventory.SourceScheduleId);
    }

    [Fact]
    public async Task ScheduleRetryBackoffIsCappedAndSavingClearsIt()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var s = await fixture.OpenAsync(clock);
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        var request = new UpsertScheduleRequest("Восстановление", 12, clock.GetUtcNow().AddMinutes(-1), "CreateDraft", true);
        var created = await schedules.CreateAsync(request, Ct);
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_schedule_retry() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.last_run_at IS NOT NULL THEN RAISE EXCEPTION 'test failure'; END IF;
                RETURN NEW;
            END; $$;
            CREATE TRIGGER fail_schedule_retry BEFORE UPDATE ON inventory_schedules
            FOR EACH ROW EXECUTE FUNCTION fail_schedule_retry();
            """, Ct);
        int[] delays = [15, 30, 60, 120, 240, 300, 300];
        for (var index = 0; index < delays.Length; index++)
        {
            await SchedulesAsync(s);
            var page = await schedules.GetPageAsync(new(1, 20), Ct);
            var failed = Assert.Single(page.Items);
            Assert.Equal(index + 1, failed.ConsecutiveFailures);
            Assert.Equal(clock.GetUtcNow().AddSeconds(delays[index]), failed.RetryAt);
            Assert.Equal(request.NextRunAt, failed.NextRunAt);
            if (index < delays.Length - 1) clock.Set(failed.RetryAt!.Value);
        }
        Assert.Empty(await s.Db.Inventories.AsNoTracking().ToListAsync(Ct));
        await s.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_schedule_retry ON inventory_schedules; DROP FUNCTION fail_schedule_retry();", Ct);
        var edited = await schedules.UpdateAsync(created.Id, request, Ct);
        Assert.Equal(0, edited.ConsecutiveFailures);
        Assert.Null(edited.RetryAt);
        await SchedulesAsync(s);
        Assert.Single(await s.Db.Inventories.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task LegacyScheduleDraftIsReusedAndLockedRowsAreSkipped()
    {
        await using var s = await fixture.OpenAsync();
        var scheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var schedule = await s.Services.GetRequiredService<IScheduleService>().CreateAsync(new("Старая", 12, scheduledAt, "CreateDraft", true), Ct);
        var legacy = new Inventory { Number = "LEGACY", Type = InventoryType.Planned, Reason = InventoryReason.AnnualReporting, ReasonDetails = $"Расписание:{schedule.Id}:{scheduledAt:yyyy-MM}", PlannedAt = scheduledAt };
        s.Db.Inventories.Add(legacy); await s.Db.SaveChangesAsync(Ct);
        await using var blockScope = s.CreateScope();
        var db = blockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        _ = await db.InventorySchedules.FromSqlInterpolated($"SELECT * FROM inventory_schedules WHERE id = {schedule.Id} FOR UPDATE").AsNoTracking().SingleAsync(Ct);
        await SchedulesAsync(s).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Null((await s.Db.InventorySchedules.AsNoTracking().SingleAsync(Ct)).LastRunAt);
        await transaction.CommitAsync(Ct);
        await SchedulesAsync(s);
        var saved = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(legacy.Id, saved.Id);
        Assert.Equal(schedule.Id, saved.SourceScheduleId);
    }

    [Fact]
    public async Task FailedEventRetryIsSerializedAuditedAndStartsOneInventory()
    {
        await using var s = await fixture.OpenAsync();
        var mol = await SeedAssetAndWorkerAsync(s);
        var events = s.Services.GetRequiredService<IIntegrationEventService>();
        var registered = await events.RegisterDismissalAsync(new("retry-event", mol, DateTimeOffset.UtcNow), Ct);
        var payload = (await ReadEventAsync(s, "retry-event")).PayloadJson;
        await s.Db.IntegrationEvents.Where(x => x.Id == registered.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, IntegrationEventStatus.Failed)
            .SetProperty(x => x.Attempts, 5)
            .SetProperty(x => x.LastError, "temporary database error")
            .SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow), Ct);
        async Task<bool> RetryAsync()
        {
            await using var scope = s.CreateScope();
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<IIntegrationEventService>().RetryAsync(registered.Id, TestSession.ActorId, Ct);
                Assert.Equal("Accepted", result.Status);
                Assert.Equal(0, result.Attempts);
                Assert.Null(result.LastError);
                return true;
            }
            catch (ConflictException) { return false; }
        }
        Assert.Single(await Task.WhenAll(RetryAsync(), RetryAsync()), x => x);
        var reset = await ReadEventAsync(s, "retry-event");
        Assert.Null(reset.ProcessedAt);
        Assert.Null(reset.NextAttemptAt);
        Assert.Equal(payload, reset.PayloadJson);
        var audit = await s.Db.AuditLogs.SingleAsync(x => x.Action == "event.retry-requested", Ct);
        Assert.Equal(TestSession.ActorId, audit.UserId);
        await EventsAsync(s);
        await EventsAsync(s);
        var inventory = await s.Db.Inventories.AsNoTracking().Include(x => x.Items).SingleAsync(Ct);
        Assert.Equal(InventoryStatus.Active, inventory.Status);
        Assert.Single(inventory.Items);
        Assert.Equal(inventory.Id, (await ReadEventAsync(s, "retry-event")).CreatedInventoryId);
        Assert.Equal(1, (await ReadEventAsync(s, "retry-event")).Attempts);
        // Use a fresh scope so the service observes the worker's committed status.
        await using var check = s.CreateScope();
        var retryService = check.ServiceProvider.GetRequiredService<IIntegrationEventService>();
        await Assert.ThrowsAsync<ConflictException>(() => retryService.RetryAsync(registered.Id, TestSession.ActorId, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => retryService.RetryAsync(Guid.NewGuid(), TestSession.ActorId, Ct));
    }

    [Fact]
    public async Task EventsWithoutAvailableWorkerPersistNeedsAttentionAndAnExplanation()
    {
        await using var s = await fixture.OpenAsync();
        var mol = await SeedAssetAndWorkerAsync(s);
        await s.Db.Users.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsBlocked, true), Ct);
        await s.Services.GetRequiredService<IIntegrationEventService>().RegisterDismissalAsync(new("no-worker", mol, DateTimeOffset.UtcNow), Ct);
        await EventsAsync(s);
        await EventsAsync(s);
        var inventory = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(InventoryStatus.NeedsAttention, inventory.Status);
        var e = await ReadEventAsync(s, "no-worker");
        Assert.Equal(IntegrationEventStatus.Processed, e.Status);
        Assert.Equal(inventory.Id, e.CreatedInventoryId);
        Assert.Contains("Для запуска нужен", e.ResultMessage);
    }

    [Fact]
    public async Task LegacyEventDraftFailureDoesNotBlockLaterDraftsAndRetriesAfterDelay()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var s = await fixture.OpenAsync(clock);
        var mol = await SeedAssetAndWorkerAsync(s);
        Inventory AddLegacyDraft(string number, DateTimeOffset createdAt)
        {
            var inventory = new Inventory
            {
                Number = number, Type = InventoryType.EventDriven, Reason = InventoryReason.MolChange,
                TargetEmployeeId = mol, PlannedAt = createdAt, CreatedAt = createdAt
            };
            s.Db.Inventories.Add(inventory);
            s.Db.IntegrationEvents.Add(new IntegrationEvent
            {
                ExternalEventId = number, Type = IntegrationEventType.MolChanged, PayloadJson = "{}",
                Status = IntegrationEventStatus.Processed, Attempts = 1, OccurredAt = createdAt,
                ProcessedAt = createdAt, CreatedInventoryId = inventory.Id
            });
            return inventory;
        }

        var broken = AddLegacyDraft("legacy-broken", clock.GetUtcNow().AddMinutes(-2));
        var healthy = AddLegacyDraft("legacy-healthy", clock.GetUtcNow().AddMinutes(-1));
        await s.Db.SaveChangesAsync(Ct);
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_legacy_draft() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.number = 'legacy-broken' AND NEW.status = 'Active' THEN RAISE EXCEPTION 'test legacy startup failure'; END IF;
                RETURN NEW;
            END; $$;
            CREATE TRIGGER fail_legacy_draft BEFORE UPDATE ON inventories FOR EACH ROW EXECUTE FUNCTION fail_legacy_draft();
            """, Ct);

        await EventsAsync(s);
        var retryAt = clock.GetUtcNow().AddSeconds(30);
        var deferred = await ReadEventAsync(s, broken.Number);
        Assert.Equal(IntegrationEventStatus.Processed, deferred.Status);
        Assert.Equal(retryAt, deferred.NextAttemptAt);
        Assert.NotNull(deferred.LastError);
        Assert.Equal(1, deferred.Attempts);
        Assert.Equal(InventoryStatus.Active, (await s.Db.Inventories.AsNoTracking().SingleAsync(x => x.Id == healthy.Id, Ct)).Status);
        Assert.Equal(InventoryStatus.Draft, (await s.Db.Inventories.AsNoTracking().SingleAsync(x => x.Id == broken.Id, Ct)).Status);
        Assert.False(await s.Db.InventoryItems.AnyAsync(x => x.InventoryId == broken.Id, Ct));
        await s.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_legacy_draft ON inventories; DROP FUNCTION fail_legacy_draft();", Ct);

        await EventsAsync(s);
        Assert.Equal(InventoryStatus.Draft, (await s.Db.Inventories.AsNoTracking().SingleAsync(x => x.Id == broken.Id, Ct)).Status);
        clock.Set(retryAt);
        await EventsAsync(s);
        var recovered = await ReadEventAsync(s, broken.Number);
        Assert.Equal(broken.Id, recovered.CreatedInventoryId);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.NextAttemptAt);
        Assert.Equal(InventoryStatus.Active, (await s.Db.Inventories.AsNoTracking().SingleAsync(x => x.Id == broken.Id, Ct)).Status);
        Assert.Single(await s.Db.InventoryItems.AsNoTracking().Where(x => x.InventoryId == broken.Id).ToListAsync(Ct));
    }

    private static async Task EventsAsync(TestSession s)
    {
        await using var scope = s.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IInventoryJobProcessor>().ProcessEventsAsync(Ct);
    }

    private static async Task SchedulesAsync(TestSession s)
    {
        await using var scope = s.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IInventoryJobProcessor>().ProcessSchedulesAsync(Ct);
    }

    private static Task<IntegrationEvent> ReadEventAsync(TestSession s, string id) => s.Db.IntegrationEvents.AsNoTracking().SingleAsync(x => x.ExternalEventId == id, Ct);

    private static async Task<Guid> SeedAssetAndWorkerAsync(TestSession s)
    {
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Имущество", false), Ct);
        var location = await s.Catalog.CreateLocationAsync(new("L", "Кабинет", dep.Id), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("M", "МОЛ", dep.Id, true, true), Ct);
        await s.Assets.CreateAsync(new("OS-1", "Стол", "01", null, 2025, new DateOnly(2026, 1, 1), 100m, dep.Id, location.Id, mol.Id), Ct);
        var workerDep = await s.Catalog.CreateDepartmentAsync(new("INV", "Инвентаризаторы", true), Ct);
        var worker = await s.Catalog.CreateEmployeeAsync(new("W", "Исполнитель", workerDep.Id, false, true), Ct);
        await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", worker.Id), Ct);
        return mol.Id;
    }
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    // PostgreSQL timestamps have microsecond precision, unlike .NET's 100ns ticks.
    public override DateTimeOffset GetUtcNow() => now.AddTicks(-(now.Ticks % 10));
    public void Set(DateTimeOffset value) => now = value;
}
