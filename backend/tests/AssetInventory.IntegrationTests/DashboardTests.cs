using AssetInventory.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class DashboardTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task DashboardCountsOnlyCurrentDiscrepanciesAndUsableRegistryRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await fixture.OpenAsync();
        var department = new Department { Code = "D", Name = "Отдел" };
        var location = new Location { Code = "L", Name = "Помещение", Department = department };
        var employee = new Employee { PersonnelNumber = "M", FullName = "МОЛ", Department = department, IsMateriallyResponsible = true, IsEmployed = true };
        var asset = new Asset { InventoryNumber = "OS-1", Name = "ОС", AccountingAccount = "01", Department = department, Location = location, MateriallyResponsibleEmployee = employee, AcceptedAt = new(2026, 1, 1), InitialCost = 100 };
        s.Db.Assets.Add(asset);
        s.Db.Assets.Add(new Asset { InventoryNumber = "LEGACY-ARCHIVED", Name = "Архивное ОС", AccountingAccount = "01", Department = department, Location = location, MateriallyResponsibleEmployee = employee, AcceptedAt = new(2026, 1, 1), InitialCost = 100, Status = AssetStatus.Archived });
        foreach (var (status, result, condition) in new[] {
            (InventoryStatus.Active, ScanResult.Found, AssetCondition.Damaged),
            (InventoryStatus.Active, ScanResult.Found, AssetCondition.Serviceable),
            (InventoryStatus.AwaitingConfirmation, ScanResult.NotFound, AssetCondition.Serviceable),
            (InventoryStatus.ReviewRequired, ScanResult.Found, AssetCondition.Unusable),
            (InventoryStatus.Completed, ScanResult.NotFound, AssetCondition.Serviceable),
            (InventoryStatus.Cancelled, ScanResult.Found, AssetCondition.Damaged),
            (InventoryStatus.Draft, ScanResult.NotFound, AssetCondition.Serviceable),
            (InventoryStatus.NeedsAttention, ScanResult.Found, AssetCondition.Damaged),
        }) s.Db.Inventories.Add(new Inventory {
            Number = status + "-" + condition, Type = InventoryType.Manual, Reason = InventoryReason.Other, Status = status, PlannedAt = DateTimeOffset.UtcNow,
            Items = [new InventoryAssetSnapshot { AssetId = asset.Id, InventoryNumber = asset.InventoryNumber, Name = asset.Name, AccountingAccount = "01", DepartmentName = department.Name, LocationName = location.Name, MateriallyResponsibleFullName = employee.FullName, Result = result, Condition = condition }],
        });
        foreach (var status in Enum.GetValues<IntegrationEventStatus>()) s.Db.IntegrationEvents.Add(new IntegrationEvent { ExternalEventId = status.ToString(), PayloadJson = "{}", Status = status, OccurredAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync(ct);
        await using var transaction = await s.Db.Database.BeginTransactionAsync(ct);
        var dashboard = await s.Monitoring.GetDashboardAsync(ct);
        Assert.Equal(1, dashboard.ActiveAssets);
        Assert.Equal(4, dashboard.ActiveInventories);
        Assert.Equal(2, dashboard.PendingEvents);
        Assert.Equal(3, dashboard.UnresolvedDiscrepancies);
    }
}
