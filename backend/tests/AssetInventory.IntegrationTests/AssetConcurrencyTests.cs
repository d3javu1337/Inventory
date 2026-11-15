using AssetInventory.Application.Assets;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class AssetConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<UpsertAssetRequest> SeedAsync(TestSession s)
    {
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Отдел", false), Ct);
        var location = await s.Catalog.CreateLocationAsync(new("L", "Комната", dep.Id), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("M", "МОЛ", dep.Id, true, true), Ct);
        return new("OS", "Стол", "01", null, 2025, new(2026, 1, 1), 100, dep.Id, location.Id, mol.Id);
    }

    [Fact]
    public async Task CompetingCreatesReturnConflictsAndPersistOnlyOneCardAndHistory()
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        async Task<bool> Create()
        {
            await using var scope = s.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<IAssetService>().CreateAsync(request, Ct); return true; }
            catch (ConflictException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Create())), x => x);
        Assert.Single(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
        Assert.Single(await s.Db.AssetAssignmentHistory.AsNoTracking().ToListAsync(Ct));
        Assert.Single(await s.Db.AuditLogs.AsNoTracking().Where(x => x.Action == "asset.created").ToListAsync(Ct));
    }

    [Fact]
    public async Task SameVersionUpdatesSerializeAtTheDatabaseAndLosingUpdateFailsThePrecondition()
    {
        await using var s = await fixture.OpenAsync();
        var request = await SeedAsync(s);
        var asset = await s.Assets.CreateAsync(request, Ct);
        await using var transaction = await s.Db.Database.BeginTransactionAsync(Ct);
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM assets WHERE id={asset.Id} FOR UPDATE", Ct);
        async Task<bool> Update(string name)
        {
            await using var scope = s.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<IAssetService>().UpdateAsync(asset.Id, asset.Version, request with { Name = name }, Ct); return true; }
            catch (PreconditionFailedException) { return false; }
        }
        var first = Update("Первый"); var second = Update("Второй");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(s.Db.Database.GetConnectionString());
        await connection.OpenAsync(timeout.Token);
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%UPDATE assets%'", connection);
            if ((long)(await command.ExecuteScalarAsync(timeout.Token))! >= 2) break;
            await Task.Delay(20, timeout.Token);
        }
        await transaction.CommitAsync(Ct);
        Assert.Single(await Task.WhenAll(first, second), x => x);
        Assert.Equal(asset.Version + 1, (await s.Db.Assets.AsNoTracking().SingleAsync(Ct)).Version);
        Assert.Single(await s.Db.AuditLogs.Where(x => x.Action == "asset.updated").ToListAsync(Ct));
    }
}
