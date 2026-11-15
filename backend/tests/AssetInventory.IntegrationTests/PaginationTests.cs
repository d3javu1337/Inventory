using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Common;
using AssetInventory.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AssetInventory.IntegrationTests;
public sealed class PaginationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task AssetLastPageIsClampedAfterArchivingAndInvalidPagingIsRejected()
    {
        await using var s = await fixture.OpenAsync();
        var dep = new Department { Code = "D", Name = "Отдел" };
        var location = new Location { Code = "L", Name = "Комната", Department = dep };
        var mol = new Employee { PersonnelNumber = "M", FullName = "МОЛ", Department = dep, IsEmployed = true, IsMateriallyResponsible = true };
        for (var i = 0; i < 21; i++) s.Db.Assets.Add(new Asset { InventoryNumber = $"OS-{i:000}", Name = "Стол", AccountingAccount = "01", AcceptedAt = new(2026, 1, 1), Department = dep, Location = location, MateriallyResponsibleEmployee = mol });
        s.Db.Assets.Add(new Asset { InventoryNumber = "LEGACY", Name = "Архивное", AccountingAccount = "01", AcceptedAt = new(2026, 1, 1), Department = dep, Location = location, MateriallyResponsibleEmployee = mol, Status = AssetStatus.Archived });
        await s.Db.SaveChangesAsync(Ct);
        var last = await s.Assets.GetAsync(new(null, null, null, false, 2, 20), Ct);
        Assert.Equal(21, last.TotalCount); Assert.Single(last.Items);
        await s.Assets.ArchiveAsync(last.Items[0].Id, Ct);
        var clamped = await s.Assets.GetAsync(new(null, null, null, false, 2, 20), Ct);
        Assert.Equal(1, clamped.Page); Assert.Equal(20, clamped.Items.Count);
        Assert.Equal(22, (await s.Assets.GetAsync(new(null, null, null, true), Ct)).TotalCount);
        foreach (var query in new[] { new AssetInventory.Contracts.Assets.AssetListQuery(null, null, null, false, int.MaxValue), new(null, null, null, false, 0), new(null, null, null, false, 1, 201), new(new string('x', 201), null, null, false) })
            await Assert.ThrowsAsync<RequestValidationException>(() => s.Assets.GetAsync(query, Ct));
    }

    [Fact]
    public async Task InventorySearchCombinesReasonAndPagingWithoutExposingOtherAssignments()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Имущество", false), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("M", "Петрова Анна", dep.Id, true, true), Ct);
        var invDep = await s.Catalog.CreateDepartmentAsync(new("I", "Инвентаризация", true), Ct);
        var firstEmployee = await s.Catalog.CreateEmployeeAsync(new("W1", "Иванов Иван", invDep.Id, false, true), Ct);
        var secondEmployee = await s.Catalog.CreateEmployeeAsync(new("W2", "Сидоров Пётр", invDep.Id, false, true), Ct);
        var first = await s.Catalog.CreateUserAsync(new("worker-one", "password-123", "InventoryEmployee", firstEmployee.Id), Ct);
        var second = await s.Catalog.CreateUserAsync(new("worker-two", "password-123", "InventoryEmployee", secondEmployee.Id), Ct);
        var inventories = Enumerable.Range(0, 45).Select(i => new Inventory {
            Number = $"INV-MOL-{i:000}", Type = InventoryType.EventDriven,
            Reason = i % 2 == 0 ? InventoryReason.MolChange : InventoryReason.MolDismissal,
            ReasonDetails = $"HR-код-{i:000}", TargetEmployeeId = mol.Id,
            Status = i == 44 ? InventoryStatus.Draft : InventoryStatus.Active, PlannedAt = DateTimeOffset.UtcNow,
            Assignments = [new InventoryAssignment { UserId = i % 3 == 0 ? second.Id : first.Id, Role = CommissionRole.Chairperson }],
        }).ToArray();
        s.Db.Inventories.AddRange(inventories);
        await s.Db.SaveChangesAsync(Ct);
        var combined = await s.Inventories.GetAsync(new("Active", "EventDriven", 2, 7, "  ПЕТРОВА  ", "MolChange"), null, Ct);
        Assert.Equal(22, combined.TotalCount);
        Assert.Equal(7, combined.Items.Count);
        Assert.All(combined.Items, i => { Assert.Equal("Петрова Анна", i.TargetEmployeeName); Assert.NotNull(i.AssigneeName); Assert.Equal("MolChange", i.Reason); });
        var owned = await s.Inventories.GetAsync(new(null, null, Search: "Петрова"), first.Id, Ct);
        Assert.Equal(30, owned.TotalCount);
        Assert.All(owned.Items, i => Assert.Equal("Иванов Иван", i.AssigneeName));
        Assert.Empty((await s.Inventories.GetAsync(new(null, null, Search: inventories[3].Id.ToString().ToUpperInvariant()), first.Id, Ct)).Items);
        Assert.Equal(inventories[4].Id, Assert.Single((await s.Inventories.GetAsync(new(null, null, Search: inventories[4].Id.ToString().ToUpperInvariant()), first.Id, Ct)).Items).Id);
        Assert.Equal(30, (await s.Inventories.GetAsync(new(null, null, Search: "WORKER-ONE"), null, Ct)).TotalCount);
        Assert.Equal(30, (await s.Inventories.GetAsync(new(null, null, Search: "ИВАНОВ"), null, Ct)).TotalCount);
        Assert.Equal(inventories[4].Id, Assert.Single((await s.Inventories.GetAsync(new(null, null, Search: "hr-КОД-004"), null, Ct)).Items).Id);
        Assert.Empty((await s.Inventories.GetAsync(new(null, null, Search: "%"), null, Ct)).Items);
        var last = await s.Inventories.GetAsync(new(null, null, 999, 20, "INV-MOL"), null, Ct);
        Assert.Equal(3, last.Page);
        Assert.Equal(5, last.Items.Count);
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.GetAsync(new(null, null, Search: new string('a', 201)), null, Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.GetAsync(new(null, null, Reason: "999"), null, Ct));
    }
    [Fact]
    public async Task InventoryPagesFilterBeforePagingKeepGlobalCountsAndRestrictAccess()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Имущество", false), Ct);
        var loc = await s.Catalog.CreateLocationAsync(new("L", "Помещение", dep.Id), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("M", "МОЛ", dep.Id, true, true), Ct);
        var workerDep = await s.Catalog.CreateDepartmentAsync(new("W", "Инвентаризация", true), Ct);
        var emp = await s.Catalog.CreateEmployeeAsync(new("W", "Исполнитель", workerDep.Id, false, true), Ct);
        var worker = await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", emp.Id), Ct);
        for (var i = 1; i <= 45; i++) await s.Assets.CreateAsync(new($"OS-{i:000}", $"Устройство {i}", "01", null, 2026, new(2026, 1, 1), 100, dep.Id, loc.Id, mol.Id), Ct);
        var inv = await s.Inventories.CreateAsync(new("Manual", "Other", "Тест пагинации", null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        var active = await s.Inventories.StartAsync(inv.Id, TestSession.ActorId, Ct);
        var list = await s.Inventories.GetAsync(new("Active", null, 999, 20), worker.Id, Ct);
        Assert.Equal(1, list.Page); Assert.Equal(inv.Id, Assert.Single(list.Items).Id);
        Assert.Empty((await s.Inventories.GetAsync(new("Completed", null), worker.Id, Ct)).Items);
        Assert.Empty((await s.Inventories.GetAsync(new("Active", null), Guid.CreateVersion7(), Ct)).Items);
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.GetAsync(new("999", null), worker.Id, Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.GetAsync(new(null, "invalid"), worker.Id, Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.GetAsync(new(null, null, 0), worker.Id, Ct));
        await s.Inventories.ScanAsync(inv.Id, new(active.Items[0].AssetId.ToString(), "Damaged", "Повреждение"), worker.Id, Ct);
        await s.Inventories.ScanAsync(inv.Id, new(active.Items[1].AssetId.ToString(), "Serviceable", null), worker.Id, Ct);
        for (var i = 0; i < 25; i++) await s.Inventories.ScanAsync(inv.Id, new($"unknown-{i:000}", "Serviceable", null), worker.Id, Ct);
        var first = await s.Inventories.GetPageAsync(inv.Id, new(1, 7, "remaining"), worker.Id, Ct);
        var second = await s.Inventories.GetPageAsync(inv.Id, new(2, 7, "remaining"), worker.Id, Ct);
        Assert.Equal(43, first.ItemsTotalCount); Assert.Equal(7, first.Items.Count);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
        Assert.Equal(new(45, 2, 43, 0, 1, 1, 25), first.Counts);
        var found = await s.Inventories.GetPageAsync(inv.Id, new(1, 7, "found"), worker.Id, Ct);
        Assert.Equal(2, found.ItemsTotalCount); Assert.All(found.Items, i => Assert.Equal("Found", i.Result));
        var search = await s.Inventories.GetPageAsync(inv.Id, new(1, 7, "all", "os-045"), null, Ct);
        Assert.Equal("OS-045", Assert.Single(search.Items).InventoryNumber); Assert.Equal(45, search.Counts!.Total);
        var history = await s.Assets.GetHistoryPageAsync(active.Items[0].AssetId, new(1, 20, "помещение"), Ct);
        Assert.Equal(1, history.TotalCount); Assert.Equal("Помещение", Assert.Single(history.Items).LocationName);
        Assert.Equal(2, (await s.Catalog.GetDepartmentsPageAsync(new(), Ct)).TotalCount);
        Assert.Equal(1, (await s.Catalog.GetLocationsPageAsync(new(1, 20, "помещение"), Ct)).TotalCount);
        Assert.Equal(1, (await s.Catalog.GetEmployeesPageAsync(new(1, 20, "исполнитель"), Ct)).TotalCount);
        Assert.Equal(1, (await s.Catalog.GetUsersPageAsync(new(1, 20, "worker"), Ct)).TotalCount);
        var qr = await s.Inventories.GetPageAsync(inv.Id, new(2, 7, "unexpected"), worker.Id, Ct);
        Assert.Equal(25, qr.UnexpectedTotalCount); Assert.Equal(7, qr.UnexpectedScans.Count); Assert.Empty(qr.Items);
        var last = await s.Inventories.GetPageAsync(inv.Id, new(999, 7, "remaining"), worker.Id, Ct);
        Assert.Equal(7, last.Page); Assert.Single(last.Items);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Inventories.GetPageAsync(inv.Id, new(), Guid.CreateVersion7(), Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.GetPageAsync(inv.Id, new(0), null, Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.GetPageAsync(inv.Id, new(1, 201), null, Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.GetPageAsync(inv.Id, new(1, 20, "invalid"), null, Ct));
    }
    [Fact]
    public async Task CatalogsSchedulesAndEventsExposeAllPagesIncludingBeyondOld500Limit()
    {
        await using var s = await fixture.OpenAsync();
        for (var i = 0; i < 45; i++) {
            s.Db.Departments.Add(new Department { Code = $"D-{i:000}", Name = $"Отдел {i:000}", IsArchived = i == 44 });
            s.Db.InventorySchedules.Add(new InventorySchedule { Name = $"Проверка {i:000}", NextRunAt = DateTimeOffset.UtcNow.AddDays(i), IsEnabled = false });
        }
        for (var i = 0; i < 505; i++) s.Db.IntegrationEvents.Add(new IntegrationEvent { ExternalEventId = $"EV-{i:000}", PayloadJson = "{}", OccurredAt = DateTimeOffset.UtcNow, ReceivedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i) });
        await s.Db.SaveChangesAsync(Ct);
        var page = await s.Catalog.GetDepartmentsPageAsync(new(3, 20), Ct);
        Assert.Equal(44, page.TotalCount); Assert.Equal(4, page.Items.Count);
        var archived = await s.Catalog.GetDepartmentsPageAsync(new(1, 20, "044", true), Ct);
        Assert.True(Assert.Single(archived.Items).IsArchived);
        var schedules = await s.Services.GetRequiredService<IScheduleService>().GetPageAsync(new(3, 20), Ct);
        Assert.Equal(45, schedules.TotalCount); Assert.Equal(5, schedules.Items.Count);
        var events = await s.Services.GetRequiredService<IIntegrationEventService>().GetPageAsync(new(26, 20), Ct);
        Assert.Equal(505, events.TotalCount); Assert.Equal(5, events.Items.Count);
        var filtered = await s.Services.GetRequiredService<IIntegrationEventService>().GetPageAsync(new(1, 20, "EV-000"), Ct);
        Assert.Equal("EV-000", Assert.Single(filtered.Items).EventId);
    }
}
