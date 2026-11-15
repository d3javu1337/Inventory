using AssetInventory.Application.Catalog;
using AssetInventory.Application.Inventories;
using AssetInventory.Infrastructure;
using AssetInventory.Infrastructure.Caching;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class ResponseCacheTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static IContainer Redis() => new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private static string Endpoint(IContainer redis) => $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}";

    [Fact]
    public async Task CatalogHitsRedisAndInvalidatesAllDependentVariants()
    {
        await using var redis = Redis(); await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        var first = await s.Catalog.CreateDepartmentAsync(new("A", "Первый", true), Ct);
        var second = await s.Catalog.CreateDepartmentAsync(new("B", "Второй", false), Ct);
        var location = await s.Catalog.CreateLocationAsync(new("L", "Кабинет", first.Id), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("E", "Иванов", first.Id, false, true), Ct);
        await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", employee.Id), Ct);
        Assert.Equal(2, (await s.Catalog.GetDepartmentsAsync(false, Ct)).Count);
        // Prove a warm read has no SQL query (all table renames stay in this disposable database).
        await s.Db.Database.ExecuteSqlRawAsync("ALTER TABLE departments RENAME TO hidden_departments", Ct);
        try { Assert.Equal(2, (await s.Catalog.GetDepartmentsAsync(false, Ct)).Count); }
        finally { await s.Db.Database.ExecuteSqlRawAsync("ALTER TABLE hidden_departments RENAME TO departments", Ct); }
        Assert.Single(await s.Catalog.GetLocationsAsync(first.Id, false, Ct));
        Assert.Empty(await s.Catalog.GetLocationsAsync(second.Id, false, Ct));
        await s.Catalog.GetEmployeesAsync(false, Ct); await s.Catalog.GetUsersAsync(Ct);
        await s.Catalog.UpdateDepartmentAsync(first.Id, new("A", "Новое название", true), Ct);
        Assert.Equal("Новое название", (await s.Catalog.GetLocationsAsync(null, false, Ct)).Single().DepartmentName);
        Assert.Equal("Новое название", (await s.Catalog.GetEmployeesAsync(false, Ct)).Single().DepartmentName);
        await s.Catalog.UpdateEmployeeAsync(employee.Id, new("E", "Петров", first.Id, false, true), Ct);
        Assert.Equal("Петров", (await s.Catalog.GetUsersAsync(Ct)).Single().EmployeeName);
        await s.Catalog.ArchiveDepartmentAsync(second.Id, Ct);
        Assert.Single(await s.Catalog.GetDepartmentsAsync(false, Ct));
        Assert.Equal(2, (await s.Catalog.GetDepartmentsAsync(true, Ct)).Count);
        await s.Catalog.ArchiveLocationAsync(location.Id, Ct);
        Assert.Empty(await s.Catalog.GetLocationsAsync(first.Id, false, Ct));
        Assert.Single(await s.Catalog.GetLocationsAsync(first.Id, true, Ct));
    }

    [Fact]
    public async Task ExternalCommitInvalidatesButRollbackDoesNotPublishUncommittedData()
    {
        await using var redis = Redis(); await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        await s.Catalog.CreateDepartmentAsync(new("A", "Первый", false), Ct);
        await s.Catalog.GetDepartmentsAsync(false, Ct);
        await using var observer = s.CreateScope(); var catalog = observer.ServiceProvider.GetRequiredService<ICatalogService>();
        await using (var transaction = await s.Db.Database.BeginTransactionAsync(Ct))
        {
            await s.Catalog.CreateDepartmentAsync(new("B", "Второй", false), Ct);
            Assert.Equal(2, (await s.Catalog.GetDepartmentsAsync(false, Ct)).Count);
            Assert.Single(await catalog.GetDepartmentsAsync(false, Ct));
            await transaction.CommitAsync(Ct);
        }
        Assert.Equal(2, (await catalog.GetDepartmentsAsync(false, Ct)).Count);
        await using (var transaction = await s.Db.Database.BeginTransactionAsync(Ct))
        {
            await s.Catalog.CreateDepartmentAsync(new("C", "Откат", false), Ct);
            Assert.Equal(3, (await s.Catalog.GetDepartmentsAsync(false, Ct)).Count);
            await transaction.RollbackAsync(Ct);
        }
        Assert.Equal(2, (await catalog.GetDepartmentsAsync(false, Ct)).Count);
    }

    [Fact]
    public async Task LateReaderCannotPoisonNewGenerationAcrossApiInstances()
    {
        await using var redis = Redis(); await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(s.Configuration); services.AddInfrastructure(s.Configuration);
        await using var other = services.BuildServiceProvider();
        var cache = s.Services.GetRequiredService<IResponseCache>(); var replica = other.GetRequiredService<IResponseCache>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = cache.GetAsync("catalog", "race", async () => { entered.SetResult(); await release.Task.WaitAsync(Ct); return "old"; }, Ct);
        await entered.Task.WaitAsync(Ct);
        await replica.InvalidateAsync(["catalog"]);
        Assert.Equal("new", await replica.GetAsync("catalog", "race", () => Task.FromResult("new"), Ct));
        release.SetResult(); Assert.Equal("old", await delayed);
        Assert.Equal("new", await cache.GetAsync<string>("catalog", "race", () => throw new InvalidOperationException("Cache miss"), Ct));
    }

    [Fact]
    public async Task DashboardInvalidatesAfterInventoryCommandsAndWorkerCommit()
    {
        await using var redis = Redis(); await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Офис", false), Ct);
        var loc = await s.Catalog.CreateLocationAsync(new("L", "Комната", dep.Id), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("M", "МОЛ", dep.Id, true, true), Ct);
        var inv = await s.Catalog.CreateDepartmentAsync(new("I", "Инвентаризация", true), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("E", "Иванов", inv.Id, false, true), Ct);
        var user = await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", employee.Id), Ct);
        Assert.Equal(0, (await s.Monitoring.GetDashboardAsync(Ct)).ActiveAssets);
        await s.Assets.CreateAsync(new("OS-1", "Стол", "01", null, 2025, new(2026, 1, 1), 100, dep.Id, loc.Id, mol.Id), Ct);
        Assert.Equal(1, (await s.Monitoring.GetDashboardAsync(Ct)).ActiveAssets);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        Assert.Equal(0, (await s.Monitoring.GetDashboardAsync(Ct)).ActiveInventories);
        await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        Assert.Equal(1, (await s.Monitoring.GetDashboardAsync(Ct)).ActiveInventories);
        await s.Inventories.FinishScanningAsync(draft.Id, user.Id, Ct);
        Assert.Equal(1, (await s.Monitoring.GetDashboardAsync(Ct)).UnresolvedDiscrepancies);
        await s.Inventories.ConfirmAsync(draft.Id, new("password-123"), user.Id, Ct);
        Assert.Equal(0, (await s.Monitoring.GetDashboardAsync(Ct)).ActiveInventories);
        var events = s.Services.GetRequiredService<IIntegrationEventService>();
        Assert.Equal(0, (await s.Monitoring.GetDashboardAsync(Ct)).UnresolvedDiscrepancies);
        await events.RegisterDismissalAsync(new("event", mol.Id, DateTimeOffset.UtcNow), Ct);
        Assert.Equal(1, (await s.Monitoring.GetDashboardAsync(Ct)).PendingEvents);
        await s.Services.GetRequiredService<IInventoryJobProcessor>().ProcessEventsAsync(Ct);
        Assert.Equal(0, (await s.Monitoring.GetDashboardAsync(Ct)).PendingEvents);
        Assert.Single(await s.Db.Inventories.AsNoTracking().Where(x => x.TargetEmployeeId == mol.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task CorruptPayloadAndRedisOutageFallBackWithoutSwallowingBusinessErrors()
    {
        await using var redis = Redis(); await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        await s.Catalog.CreateDepartmentAsync(new("A", "Первый", false), Ct);
        await s.Catalog.GetDepartmentsAsync(false, Ct);
        using var connection = await ConnectionMultiplexer.ConnectAsync(Endpoint(redis));
        foreach (var key in connection.GetServer(Endpoint(redis)).Keys(pattern: "asset-inventory:response:*:departments:False"))
            await connection.GetDatabase().StringSetAsync(key, "invalid-json", TimeSpan.FromSeconds(30));
        Assert.Single(await s.Catalog.GetDepartmentsAsync(false, Ct));
        await redis.StopAsync(Ct);
        Assert.Single(await s.Catalog.GetDepartmentsAsync(false, Ct));
        await s.Catalog.CreateDepartmentAsync(new("B", "Второй", false), Ct);
        Assert.Equal(2, (await s.Catalog.GetDepartmentsAsync(false, Ct)).Count);
        var cache = s.Services.GetRequiredService<IResponseCache>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync<string>("catalog", "failure", () => throw new InvalidOperationException("Business error"), Ct));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync("catalog", "cancel", () => Task.FromResult("value"), cancellation.Token));
    }

    [Fact]
    public async Task CatalogDoesNotResurrectAnOldCacheEntryAfterRedisRecovers()
    {
        await using var redis = Redis(); await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        var department = await s.Catalog.CreateDepartmentAsync(new("A", "До сбоя", false), Ct);
        Assert.Equal("До сбоя", (await s.Catalog.GetDepartmentsAsync(false, Ct)).Single().Name);
        var cache = s.Services.GetRequiredService<IResponseCache>();
        using var control = await ConnectionMultiplexer.ConnectAsync(Endpoint(redis) + ",allowAdmin=true");
        await control.GetDatabase().ExecuteAsync("CLIENT", "PAUSE", 1500, "ALL");

        // The failed Redis read opens the short cooldown. The committed edit
        // below then cannot send an invalidation before Redis starts responding.
        Assert.Equal("fallback", await cache.GetAsync("catalog", "outage-probe", () => Task.FromResult("fallback"), Ct));
        await s.Catalog.UpdateDepartmentAsync(department.Id, new("A", "После сбоя", false), Ct);
        Assert.Equal("После сбоя", (await s.Catalog.GetDepartmentsAsync(false, Ct)).Single().Name);
        await Task.Delay(TimeSpan.FromMilliseconds(5250), Ct);
        await control.GetDatabase().PingAsync();
        Assert.Equal("После сбоя", (await s.Catalog.GetDepartmentsAsync(false, Ct)).Single().Name);
    }

    [Fact]
    public async Task NullAndEmptyResponsesAreCachedAndDashboardExpiresAfterFiveSeconds()
    {
        await using var redis = Redis(); await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        Assert.Null(await s.Catalog.GetOrganizationAsync(Ct));
        await s.Db.Database.ExecuteSqlRawAsync("ALTER TABLE organizations RENAME TO hidden_organizations", Ct);
        try { Assert.Null(await s.Catalog.GetOrganizationAsync(Ct)); }
        finally { await s.Db.Database.ExecuteSqlRawAsync("ALTER TABLE hidden_organizations RENAME TO organizations", Ct); }
        var cache = s.Services.GetRequiredService<IResponseCache>(); var calls = 0;
        Task<int[]> Load() { calls++; return Task.FromResult(Array.Empty<int>()); }
        Assert.Empty(await cache.GetAsync("dashboard", "ttl", Load, Ct));
        Assert.Empty(await cache.GetAsync("dashboard", "ttl", Load, Ct)); Assert.Equal(1, calls);
        await Task.Delay(5100, Ct);
        Assert.Empty(await cache.GetAsync("dashboard", "ttl", Load, Ct)); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SharedRedisIsolatesDatabasesAndSynchronousSaveInvalidates()
    {
        await using var redis = Redis(); await redis.StartAsync(Ct);
        await using var first = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        await using var second = await fixture.OpenAsync(redisConnection: Endpoint(redis));
        var department = await first.Catalog.CreateDepartmentAsync(new("A", "Первый", false), Ct);
        Assert.Single(await first.Catalog.GetDepartmentsAsync(false, Ct));
        Assert.Empty(await second.Catalog.GetDepartmentsAsync(false, Ct));
        var tracked = await first.Db.Departments.SingleAsync(x => x.Id == department.Id, Ct);
        tracked.Name = "Синхронное изменение"; first.Db.SaveChanges();
        Assert.Equal("Синхронное изменение", (await first.Catalog.GetDepartmentsAsync(false, Ct)).Single().Name);
        Assert.Empty(await second.Catalog.GetDepartmentsAsync(false, Ct));
        Assert.Null(await first.Catalog.GetOrganizationAsync(Ct));
        await first.Catalog.UpsertOrganizationAsync(new("Организация", "1234567890", null, null, null, null), Ct);
        Assert.Equal("Организация", (await first.Catalog.GetOrganizationAsync(Ct))!.Name);
        Assert.Null(await second.Catalog.GetOrganizationAsync(Ct));
    }
}
