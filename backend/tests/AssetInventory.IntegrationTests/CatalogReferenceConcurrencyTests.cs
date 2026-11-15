using System.Text;
using AssetInventory.Application.Assets;
using AssetInventory.Application.Catalog;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Assets;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class CatalogReferenceConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<UpsertAssetRequest> SeedAsync(TestSession s)
    {
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Отдел", false), Ct);
        var location = await s.Catalog.CreateLocationAsync(new("L", "Комната", dep.Id), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("M", "МОЛ", dep.Id, true, true), Ct);
        return new("OS-1", "Стол", "01", null, 2025, new(2026, 1, 1), 100, dep.Id, location.Id, mol.Id);
    }

    private static async Task<IDbContextTransaction> PauseInsertsAsync(TestSession s, string table)
    {
        if (table is not ("assets" or "locations")) throw new ArgumentException("Unsupported fixture table");
        await s.Db.Database.ExecuteSqlRawAsync("CREATE FUNCTION pause_reference_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_xact_lock(736412893); RETURN NEW; END $$;", Ct);
        var trigger = table == "assets"
            ? "CREATE TRIGGER pause_reference_insert BEFORE INSERT ON assets FOR EACH ROW EXECUTE FUNCTION pause_reference_insert();"
            : "CREATE TRIGGER pause_reference_insert BEFORE INSERT ON locations FOR EACH ROW EXECUTE FUNCTION pause_reference_insert();";
        await s.Db.Database.ExecuteSqlRawAsync(trigger, Ct);
        var barrier = await s.Db.Database.BeginTransactionAsync(Ct);
        await s.Db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(736412893)", Ct);
        return barrier;
    }

    private static async Task WaitForLockAsync(TestSession s, string query, Task? completedInstead = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(s.Db.Database.GetConnectionString());
        await connection.OpenAsync(timeout.Token);
        while (completedInstead?.IsCompleted != true)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE @query", connection);
            command.Parameters.AddWithValue("query", "%" + query + "%");
            if ((long)(await command.ExecuteScalarAsync(timeout.Token))! > 0) return;
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task ConcurrentFirstOrganizationSavesKeepOneRecordAndOrderedAudit()
    {
        await using var s = await fixture.OpenAsync();
        await using var barrier = await s.Db.Database.BeginTransactionAsync(Ct);
        var first = await s.Catalog.UpsertOrganizationAsync(new("Первая организация", "1234567890", null, null, null, null), Ct);

        await using var scope = s.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();
        var saving = catalog.UpsertOrganizationAsync(new("Уточнённые реквизиты", "123456789012", null, null, null, null), Ct);
        await WaitForLockAsync(s, "LOCK TABLE organizations", saving);
        await barrier.CommitAsync(Ct);

        var second = await saving;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(second, await catalog.GetOrganizationAsync(Ct));
        Assert.Equal(1, await s.Db.Organizations.CountAsync(Ct));
        var entityId = first.Id.ToString();
        var logs = await s.Db.AuditLogs.AsNoTracking().Where(x => x.EntityId == entityId).OrderBy(x => x.CreatedAt).ToListAsync(Ct);
        Assert.Collection(logs, created => Assert.Equal("organization.created", created.Action), updated => Assert.Equal("organization.updated", updated.Action));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartingOrAssigningInventoryRevalidatesAWorkerAfterPendingBlock(bool replacing)
    {
        await using var s = await fixture.OpenAsync();
        await s.Assets.CreateAsync(await SeedAsync(s), Ct);
        var draftRequest = new CreateInventoryRequest("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow);
        var draft = replacing ? await s.Inventories.CreateAsync(draftRequest, TestSession.ActorId, Ct) : null;
        var worker = await CreateWorkerAsync(s.Catalog, "FIRST");
        draft ??= await s.Inventories.CreateAsync(draftRequest, TestSession.ActorId, Ct);

        await using var blockScope = s.CreateScope();
        var blockDb = blockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var barrier = await blockDb.Database.BeginTransactionAsync(Ct);
        await blockScope.ServiceProvider.GetRequiredService<ICatalogService>().SetUserBlockedAsync(worker, true, Ct);
        await using var commandScope = s.CreateScope();
        var service = commandScope.ServiceProvider.GetRequiredService<IInventoryService>();
        var command = replacing
            ? service.ReplaceAssigneeAsync(draft.Id, new(Guid.Empty, worker), TestSession.ActorId, Ct)
            : service.StartAsync(draft.Id, TestSession.ActorId, Ct);
        await WaitForLockAsync(s, "pg_advisory_xact_lock_shared(736412891)", command);
        await barrier.CommitAsync(Ct);

        if (replacing) await Assert.ThrowsAsync<RequestValidationException>(() => command);
        else await Assert.ThrowsAsync<ConflictException>(() => command);
        var saved = await s.Db.Inventories.AsNoTracking().Include(x => x.Assignments).Include(x => x.Items).SingleAsync(Ct);
        Assert.Equal(InventoryStatus.Draft, saved.Status);
        Assert.Empty(saved.Items);
        Assert.Equal(replacing ? 0 : 1, saved.Assignments.Count);
    }

    [Fact]
    public async Task EventAutostartChoosesTheAvailableWorkerAfterPendingAccessChanges()
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        await s.Assets.CreateAsync(request, Ct);
        var original = await CreateWorkerAsync(s.Catalog, "FIRST");
        await s.Services.GetRequiredService<IIntegrationEventService>().RegisterDismissalAsync(new("worker-change", request.MateriallyResponsibleEmployeeId, DateTimeOffset.UtcNow), Ct);

        await using var accessScope = s.CreateScope();
        var accessDb = accessScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalog = accessScope.ServiceProvider.GetRequiredService<ICatalogService>();
        await using var barrier = await accessDb.Database.BeginTransactionAsync(Ct);
        await catalog.SetUserBlockedAsync(original, true, Ct);
        var replacement = await CreateWorkerAsync(catalog, "SECOND");

        await using var workerScope = s.CreateScope();
        var processing = workerScope.ServiceProvider.GetRequiredService<IInventoryJobProcessor>().ProcessEventsAsync(Ct);
        await WaitForLockAsync(s, "pg_advisory_xact_lock_shared(736412891)", processing);
        await barrier.CommitAsync(Ct);
        await processing;

        var inventory = await s.Db.Inventories.AsNoTracking().Include(x => x.Assignments).Include(x => x.Items).SingleAsync(Ct);
        Assert.Equal(InventoryStatus.Active, inventory.Status);
        Assert.Equal(replacement, Assert.Single(inventory.Assignments).UserId);
        Assert.Single(inventory.Items);
        Assert.Equal(IntegrationEventStatus.Processed, (await s.Db.IntegrationEvents.AsNoTracking().SingleAsync(Ct)).Status);
    }

    private static async Task<Guid> CreateWorkerAsync(ICatalogService catalog, string suffix)
    {
        var department = await catalog.CreateDepartmentAsync(new("INV-" + suffix, "Инвентаризация", true), Ct);
        var employee = await catalog.CreateEmployeeAsync(new("W-" + suffix, "Исполнитель", department.Id, false, true), Ct);
        return (await catalog.CreateUserAsync(new("worker-" + suffix, "password-123", "InventoryEmployee", employee.Id), Ct)).Id;
    }

    [Theory]
    [InlineData("archive-location", false)]
    [InlineData("move-location", false)]
    [InlineData("demote-mol", false)]
    [InlineData("archive-mol", false)]
    [InlineData("archive-location", true)]
    [InlineData("move-location", true)]
    [InlineData("demote-mol", true)]
    [InlineData("archive-mol", true)]
    public async Task SavingAssetAndChangingItsReferencesCannotBothCommit(string change, bool import)
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        var other = await s.Catalog.CreateDepartmentAsync(new("OTHER", "Другой отдел", false), Ct);
        await using var barrier = await PauseInsertsAsync(s, "assets");
        await using var writeScope = s.CreateScope();
        async Task Write()
        {
            if (!import) await writeScope.ServiceProvider.GetRequiredService<IAssetService>().CreateAsync(request, Ct);
            else {
                const string csv = "inventoryNumber;name;accountingAccount;serialNumber;manufactureYear;acceptedAt;initialCost;departmentCode;locationCode;molPersonnelNumber\nOS-1;Стол;01;;2025;2026-01-01;100.00;D;L;M\n";
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv + "OS-2;Стул;01;;2025;2026-01-01;200.00;D;L;M\n"));
                var result = await writeScope.ServiceProvider.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.csv", true, Ct);
                Assert.Equal(2, result.AppliedRows);
            }
        }
        var writing = Write();
        await WaitForLockAsync(s, "INSERT INTO assets");
        await using var catalogScope = s.CreateScope();
        var catalog = catalogScope.ServiceProvider.GetRequiredService<ICatalogService>();
        async Task<bool> ChangeReference()
        {
            try {
                switch (change) {
                    case "archive-location": await catalog.ArchiveLocationAsync(request.LocationId, Ct); break;
                    case "move-location": await catalog.UpdateLocationAsync(request.LocationId, new("L", "Комната", other.Id), Ct); break;
                    case "demote-mol": await catalog.UpdateEmployeeAsync(request.MateriallyResponsibleEmployeeId, new("M", "МОЛ", request.DepartmentId, false, true), Ct); break;
                    case "archive-mol": await catalog.ArchiveEmployeeAsync(request.MateriallyResponsibleEmployeeId, Ct); break;
                    default: throw new ArgumentException(change);
                }
                return true;
            }
            catch (ConflictException) { return false; }
        }
        var changing = ChangeReference();
        await WaitForLockAsync(s, "pg_advisory_xact_lock(736412891)", changing);
        await barrier.CommitAsync(Ct);
        await writing;
        Assert.False(await changing);
        var assets = await s.Db.Assets.AsNoTracking().Include(x => x.Location).Include(x => x.MateriallyResponsibleEmployee).ToListAsync(Ct);
        Assert.Equal(import ? 2 : 1, assets.Count);
        Assert.All(assets, asset => {
            Assert.False(asset.Location.IsArchived);
            Assert.Equal(asset.DepartmentId, asset.Location.DepartmentId);
            Assert.False(asset.MateriallyResponsibleEmployee.IsArchived);
            Assert.True(asset.MateriallyResponsibleEmployee.IsMateriallyResponsible);
        });
        Assert.Equal(assets.Count, await s.Db.AssetAssignmentHistory.AsNoTracking().CountAsync(Ct));
    }

    [Fact]
    public async Task CatalogChangeCommittingFirstIsRevalidatedBeforeAssetInsert()
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        await using var barrier = await s.Db.Database.BeginTransactionAsync(Ct);
        await s.Catalog.ArchiveLocationAsync(request.LocationId, Ct);
        await using var scope = s.CreateScope();
        var creating = scope.ServiceProvider.GetRequiredService<IAssetService>().CreateAsync(request, Ct);
        await WaitForLockAsync(s, "pg_advisory_xact_lock_shared(736412891)");
        await barrier.CommitAsync(Ct);
        await Assert.ThrowsAsync<NotFoundException>(() => creating);
        Assert.Empty(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(await s.Db.AssetAssignmentHistory.AsNoTracking().ToListAsync(Ct));
        Assert.False(await s.Db.AuditLogs.AnyAsync(x => x.Action == "asset.created", Ct));
    }

    [Fact]
    public async Task ImportResolvesMolNumberAfterWaitingForItsReassignment()
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        var replacement = await s.Catalog.CreateEmployeeAsync(new("NEW", "Другой МОЛ", request.DepartmentId, true, true), Ct);
        await using var changeScope = s.CreateScope();
        var changeDb = changeScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalog = changeScope.ServiceProvider.GetRequiredService<ICatalogService>();
        await using var barrier = await changeDb.Database.BeginTransactionAsync(Ct);
        await catalog.UpdateEmployeeAsync(request.MateriallyResponsibleEmployeeId, new("OLD", "МОЛ", request.DepartmentId, true, true), Ct);
        await catalog.UpdateEmployeeAsync(replacement.Id, new("M", "Другой МОЛ", request.DepartmentId, true, true), Ct);

        await using var importScope = s.CreateScope();
        var importing = ImportSingleAssetAsync(importScope, "D", true, Ct);
        await WaitForLockAsync(s, "pg_advisory_xact_lock_shared(736412891)");
        await barrier.CommitAsync(Ct);

        Assert.Equal(1, (await importing).AppliedRows);
        Assert.Equal(replacement.Id, (await s.Db.Assets.AsNoTracking().SingleAsync(Ct)).MateriallyResponsibleEmployeeId);
        Assert.Equal(replacement.Id, (await s.Db.AssetAssignmentHistory.AsNoTracking().SingleAsync(Ct)).MateriallyResponsibleEmployeeId);
    }

    [Fact]
    public async Task CreatingDepartmentWithSimilarCodeWaitsForImportCommit()
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        await s.Catalog.UpdateDepartmentAsync(request.DepartmentId, new("De", "Отдел", false), Ct);
        await using var barrier = await PauseInsertsAsync(s, "assets");
        await using var importScope = s.CreateScope();
        var importing = ImportSingleAssetAsync(importScope, "dE", true, Ct);
        await WaitForLockAsync(s, "INSERT INTO assets");

        await using var createScope = s.CreateScope();
        var creating = createScope.ServiceProvider.GetRequiredService<ICatalogService>().CreateDepartmentAsync(new("de", "Другой отдел", false), Ct);
        await WaitForLockAsync(s, "pg_advisory_xact_lock(736412891)", creating);
        var createdBeforeImportCommit = creating.IsCompleted;
        await barrier.CommitAsync(Ct);

        Assert.Equal(1, (await importing).AppliedRows);
        await creating;
        Assert.False(createdBeforeImportCommit);
        Assert.Equal(request.DepartmentId, (await s.Db.Assets.AsNoTracking().SingleAsync(Ct)).DepartmentId);
        var preview = await ImportSingleAssetAsync(importScope, "dE", false, Ct);
        Assert.Equal(1, preview.InvalidRows);
        Assert.Contains("неоднозначный код", Assert.Single(preview.Rows).Error);
    }

    [Fact]
    public async Task ImportPreviewDoesNotWaitForWriteLocks()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        await using var barrier = await s.Db.Database.BeginTransactionAsync(Ct);
        await s.Db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(736412889), pg_advisory_xact_lock(736412891)", Ct);
        await using var importScope = s.CreateScope();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var preview = await ImportSingleAssetAsync(importScope, "D", false, timeout.Token);
        Assert.Equal(1, preview.ValidRows);
        Assert.Equal(0, preview.AppliedRows);
        Assert.Empty(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
    }

    private static async Task<ImportPreviewResponse> ImportSingleAssetAsync(AsyncServiceScope scope, string departmentCode, bool apply, CancellationToken ct)
    {
        const string header = "inventoryNumber;name;accountingAccount;serialNumber;manufactureYear;acceptedAt;initialCost;departmentCode;locationCode;molPersonnelNumber\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header + $"OS-1;Стол;01;;2025;2026-01-01;100.00;{departmentCode};L;M\n"));
        return await scope.ServiceProvider.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.csv", apply, ct);
    }

    [Fact]
    public async Task AssetReassignmentKeepsItsTargetLocationActiveUntilCommit()
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        var asset = await s.Assets.CreateAsync(request, Ct);
        var target = await s.Catalog.CreateLocationAsync(new("TARGET", "Целевая комната", request.DepartmentId), Ct);
        await using var barrier = await s.Db.Database.BeginTransactionAsync(Ct);
        // Allow the new history row's foreign-key check to take KEY SHARE.
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM assets WHERE id={asset.Id} FOR NO KEY UPDATE", Ct);
        await using var writeScope = s.CreateScope();
        var updating = writeScope.ServiceProvider.GetRequiredService<IAssetService>().UpdateAsync(asset.Id, asset.Version, request with { LocationId = target.Id }, Ct);
        await WaitForLockAsync(s, "UPDATE assets");
        await using var catalogScope = s.CreateScope();
        async Task<bool> Archive()
        {
            try { await catalogScope.ServiceProvider.GetRequiredService<ICatalogService>().ArchiveLocationAsync(target.Id, Ct); return true; }
            catch (ConflictException) { return false; }
        }
        var archiving = Archive();
        await WaitForLockAsync(s, "pg_advisory_xact_lock(736412891)", archiving);
        await barrier.CommitAsync(Ct);
        Assert.Equal(target.Id, (await updating).LocationId);
        Assert.False(await archiving);
        Assert.Equal(2, await s.Db.AssetAssignmentHistory.CountAsync(Ct));
        Assert.Equal(1, await s.Db.AssetAssignmentHistory.CountAsync(x => x.ValidTo == null, Ct));
    }

    [Fact]
    public async Task EarlierTrackedCopyCannotMakeArchivedLocationEditable()
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        await using (var scope = s.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ICatalogService>().ArchiveLocationAsync(request.LocationId, Ct);
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.UpdateLocationAsync(request.LocationId, new("EDITED", "Изменённая", request.DepartmentId), Ct));
        var location = await s.Db.Locations.AsNoTracking().SingleAsync(Ct);
        Assert.True(location.IsArchived); Assert.Equal("L", location.Code);
    }

    [Fact]
    public async Task CreatingLocationAndArchivingItsDepartmentCannotBothCommit()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Отдел", false), Ct);
        await using var barrier = await PauseInsertsAsync(s, "locations");
        await using var createScope = s.CreateScope();
        var creating = createScope.ServiceProvider.GetRequiredService<ICatalogService>().CreateLocationAsync(new("L", "Комната", dep.Id), Ct);
        await WaitForLockAsync(s, "INSERT INTO locations");
        await using var archiveScope = s.CreateScope();
        async Task<bool> Archive()
        {
            try { await archiveScope.ServiceProvider.GetRequiredService<ICatalogService>().ArchiveDepartmentAsync(dep.Id, Ct); return true; }
            catch (ConflictException) { return false; }
        }
        var archiving = Archive();
        await WaitForLockAsync(s, "pg_advisory_xact_lock(736412891)", archiving);
        await barrier.CommitAsync(Ct);
        await creating;
        Assert.False(await archiving);
        Assert.False((await s.Db.Departments.AsNoTracking().SingleAsync(Ct)).IsArchived);
    }
}
