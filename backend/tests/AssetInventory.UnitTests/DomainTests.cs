using AssetInventory.Domain;
using Xunit;

namespace AssetInventory.UnitTests;

public sealed class DomainTests
{
    [Fact]
    public void NewEntitiesUseUuidV7AndActiveAssetDefaults()
    {
        var asset = new Asset { InventoryNumber = "OS-1", Name = "Тест", AccountingAccount = "01", AcceptedAt = DateOnly.FromDateTime(DateTime.UtcNow), InitialCost = 1, DepartmentId = Guid.CreateVersion7(), LocationId = Guid.CreateVersion7(), MateriallyResponsibleEmployeeId = Guid.CreateVersion7() };
        Assert.Equal(AssetStatus.InUse, asset.Status);
        Assert.Equal(0, asset.Version);
        Assert.Equal(7, asset.Id.Version);
    }

    [Fact]
    public void NewInventoryStartsAsDraftAndScansPending()
    {
        var inventory = new Inventory { Number = "INV-1", Type = InventoryType.Planned, Reason = InventoryReason.AnnualReporting, PlannedAt = DateTimeOffset.UtcNow };
        var item = new InventoryAssetSnapshot { InventoryId = inventory.Id, AssetId = Guid.CreateVersion7(), InventoryNumber = "OS-1", Name = "Тест", AccountingAccount = "01", AcceptedAt = DateOnly.FromDateTime(DateTime.UtcNow), InitialCost = 10, DepartmentName = "D", LocationName = "L", MateriallyResponsibleFullName = "МОЛ" };
        Assert.Equal(InventoryStatus.Draft, inventory.Status);
        Assert.Equal(ScanResult.Pending, item.Result);
    }
}
