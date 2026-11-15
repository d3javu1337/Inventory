using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AssetInventory.Api.Endpoints;
using AssetInventory.Api.Middleware;
using AssetInventory.Application.Auth;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Auth;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using AssetInventory.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class ReviewRegressionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LogoutOfRotatedAncestorRevokesOnlyItsSessionChain()
    {
        await using var s = await fixture.OpenAsync();
        var initial = await LoginAsync(s);
        var otherDevice = await s.Auth.LoginAsync(new("admin", "password-123", "other-device"), Ct);
        var child = await s.Auth.RefreshAsync(new(initial.RefreshToken, null), Ct);
        var grandchild = await s.Auth.RefreshAsync(new(child.RefreshToken, null), Ct);
        await s.Auth.LogoutAsync(initial.RefreshToken, Ct);
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.RefreshAsync(new(grandchild.RefreshToken, null), Ct));
        Assert.NotNull(await s.Auth.RefreshAsync(new(otherDevice.RefreshToken, null), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AncestorRevocationWaitsForDescendantRotation(bool logout)
    {
        await using var s = await fixture.OpenAsync();
        var initial = await LoginAsync(s);
        var child = await s.Auth.RefreshAsync(new(initial.RefreshToken, null), Ct);
        await using var blocker = s.CreateScope();
        var blockDb = blocker.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await blockDb.Database.BeginTransactionAsync(Ct);
        var childHash = Hash(child.RefreshToken);
        _ = await blockDb.RefreshTokens.FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {childHash} FOR UPDATE")
            .AsNoTracking().SingleAsync(Ct);

        await using var rotationScope = s.CreateScope();
        var rotation = rotationScope.ServiceProvider.GetRequiredService<IAuthService>().RefreshAsync(new(child.RefreshToken, null), Ct);
        await WaitForLockWaitersAsync(s, "refresh_tokens", 1);
        await using var revocationScope = s.CreateScope();
        var auth = revocationScope.ServiceProvider.GetRequiredService<IAuthService>();
        var revocation = logout ? auth.LogoutAsync(initial.RefreshToken, Ct) : ReplayAsync(auth, initial.RefreshToken);
        await WaitForLockWaitersAsync(s, "refresh_tokens", 2);
        await transaction.CommitAsync(Ct);
        var grandchild = await rotation;
        await revocation;
        await using var verifyScope = s.CreateScope();
        await Assert.ThrowsAsync<UnauthorizedException>(() => verifyScope.ServiceProvider.GetRequiredService<IAuthService>()
            .RefreshAsync(new(grandchild.RefreshToken, null), Ct));
    }

    [Fact]
    public async Task ConcurrentLogoutAndRefreshCannotLeaveAUsableReplacement()
    {
        await using var s = await fixture.OpenAsync();
        var initial = await LoginAsync(s);
        await using var rotationScope = s.CreateScope();
        await using var logoutScope = s.CreateScope();
        async Task RotateAsync()
        {
            try { await rotationScope.ServiceProvider.GetRequiredService<IAuthService>().RefreshAsync(new(initial.RefreshToken, null), Ct); }
            catch (UnauthorizedException) { }
        }
        await Task.WhenAll(RotateAsync(), logoutScope.ServiceProvider.GetRequiredService<IAuthService>().LogoutAsync(initial.RefreshToken, Ct));
        Assert.True(await s.Db.RefreshTokens.AsNoTracking().AllAsync(x => x.RevokedAt != null, Ct));
    }

    [Fact]
    public async Task NewDraftHasAnAssigneeWhoCanBeReplacedBeforeStart()
    {
        await using var s = await fixture.OpenAsync();
        var first = await WorkerAsync(s, "1");
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        Assert.Equal(first, Assert.Single(draft.Assignments).UserId);
        var second = await WorkerAsync(s, "2");
        var replaced = await s.Inventories.ReplaceAssigneeAsync(draft.Id, new(first, second), TestSession.ActorId, Ct);
        Assert.Equal(second, Assert.Single(replaced.Assignments).UserId);
    }

    [Fact]
    public async Task EmptyDraftCanBeAssignedAfterAnEmployeeBecomesAvailable()
    {
        await using var s = await fixture.OpenAsync();
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        Assert.Empty(draft.Assignments);
        var worker = await WorkerAsync(s, "1");
        var assigned = await s.Inventories.ReplaceAssigneeAsync(draft.Id, new(Guid.Empty, worker), TestSession.ActorId, Ct);
        Assert.Equal(worker, Assert.Single(assigned.Assignments).UserId);
    }

    [Fact]
    public async Task RejectedInventoryCanReplaceUnavailableWorkerBeforeRescanning()
    {
        await using var s = await fixture.OpenAsync();
        var first = await WorkerAsync(s, "1");
        var inventory = await ActiveInventoryAsync(s, first, 1);
        await s.Inventories.FinishScanningAsync(inventory.Id, first, Ct);
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Inventories.RejectAsync(inventory.Id, new(null!, "Повторить"), first, Ct));
        await s.Inventories.RejectAsync(inventory.Id, new("password-123", "Повторить"), first, Ct);
        await s.Catalog.SetUserBlockedAsync(first, true, Ct);
        var second = await WorkerAsync(s, "2");
        var replaced = await s.Inventories.ReplaceAssigneeAsync(inventory.Id, new(first, second), TestSession.ActorId, Ct);
        Assert.Equal("ReviewRequired", replaced.Status);
        var restarted = await s.Inventories.StartAsync(inventory.Id, TestSession.ActorId, Ct);
        Assert.Equal(second, Assert.Single(restarted.Assignments).UserId);
        Assert.Equal("Pending", Assert.Single(restarted.Items).Result);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Inventories.GetByIdAsync(inventory.Id, first, Ct));
    }

    [Fact]
    public async Task RejectedInventoryCanBeRescannedWithoutChangingItsAccountingSnapshot()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await WorkerAsync(s, "1");
        var inventory = await ActiveInventoryAsync(s, worker, 2);
        var first = inventory.Items.First();
        await s.Inventories.ScanAsync(inventory.Id, new("QR прошлого обхода", "Serviceable", null), worker, Ct);
        await s.Inventories.ScanAsync(inventory.Id, new(first.AssetId.ToString(), "Damaged", "Ошибочное состояние"), worker, Ct);
        await s.Inventories.FinishScanningAsync(inventory.Id, worker, Ct);
        await s.Inventories.RejectAsync(inventory.Id, new("password-123", "Исправить состояние"), worker, Ct);
        var restarted = await s.Inventories.StartAsync(inventory.Id, TestSession.ActorId, Ct);
        Assert.Null(restarted.ScanningFinishedAt);
        Assert.Empty(restarted.UnexpectedScans);
        Assert.All(restarted.Items, item => { Assert.Equal("Pending", item.Result); Assert.Null(item.Condition); Assert.Null(item.Comment); Assert.Null(item.ScannedAt); });
        Assert.Equal(first.Name, restarted.Items.Single(x => x.AssetId == first.AssetId).Name);
        Assert.Equal(first.InitialCost, restarted.Items.Single(x => x.AssetId == first.AssetId).InitialCost);
        Assert.Equal("Pending", Assert.Single(restarted.Assignments).ConfirmationStatus);
        var corrected = await s.Inventories.ScanAsync(inventory.Id, new(first.AssetId.ToString(), "Serviceable", null), worker, Ct);
        Assert.False(corrected.Duplicate);
        Assert.Equal("Serviceable", corrected.Asset!.Condition);
        var resetAudit = await s.Db.AuditLogs.SingleAsync(x => x.Action == "inventory.observations-reset", Ct);
        var previousResults = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resetAudit.DataJson!);
        Assert.Equal("Ошибочное состояние", previousResults.EnumerateArray().Single(x => x.GetProperty("AssetId").GetGuid() == first.AssetId).GetProperty("Comment").GetString());
        var unexpectedAudit = await s.Db.AuditLogs.SingleAsync(x => x.Action == "inventory.unexpected-observations-reset", Ct);
        using var unexpected = System.Text.Json.JsonDocument.Parse(unexpectedAudit.DataJson!);
        Assert.Equal("QR прошлого обхода", unexpected.RootElement[0].GetProperty("RawValue").GetString());
        await s.Inventories.ScanAsync(inventory.Id, new("QR нового обхода", "Serviceable", null), worker, Ct);
        Assert.Equal("QR нового обхода", Assert.Single((await s.Inventories.GetByIdAsync(inventory.Id, worker, Ct)).UnexpectedScans).RawValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentScansAreSerializedAndFinalRetriesAreIdempotent(bool sameAsset)
    {
        await using var s = await fixture.OpenAsync();
        var worker = await WorkerAsync(s, "1");
        var inventory = await ActiveInventoryAsync(s, worker, sameAsset ? 1 : 2);
        var ids = inventory.Items.Select(x => x.AssetId).ToArray();
        await using var blocker = s.CreateScope();
        var blockDb = blocker.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await blockDb.Database.BeginTransactionAsync(Ct);
        _ = await blockDb.Inventories.FromSqlInterpolated($"SELECT * FROM inventories WHERE id = {inventory.Id} FOR UPDATE").AsNoTracking().SingleAsync(Ct);
        await using var firstScope = s.CreateScope();
        await using var secondScope = s.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IInventoryService>().ScanAsync(inventory.Id, new(ids[0].ToString(), "Serviceable", null), worker, Ct);
        var second = secondScope.ServiceProvider.GetRequiredService<IInventoryService>().ScanAsync(inventory.Id, new(ids[^1].ToString(), "Damaged", "Повреждение"), worker, Ct);
        await WaitForLockWaitersAsync(s, "inventories", 2);
        await transaction.CommitAsync(Ct);
        var results = await Task.WhenAll(first, second);
        Assert.Equal(sameAsset ? 1 : 0, results.Count(x => x.Duplicate));
        await using var verify = s.CreateScope();
        var service = verify.ServiceProvider.GetRequiredService<IInventoryService>();
        var saved = await service.GetByIdAsync(inventory.Id, worker, Ct);
        Assert.Equal("AwaitingConfirmation", saved.Status);
        Assert.All(saved.Items, item => Assert.Equal("Found", item.Result));
        if (sameAsset) Assert.Equal(results.Single(x => !x.Duplicate).Asset!.Condition, Assert.Single(saved.Items).Condition);
        Assert.True((await service.ScanAsync(inventory.Id, new(ids[0].ToString(), "Unusable", "Повтор"), worker, Ct)).Duplicate);
    }

    [Fact]
    public async Task ScanLoadsOnlyTheMatchingSnapshot()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await WorkerAsync(s, "1");
        var inventory = await ActiveInventoryAsync(s, worker, 24);
        var assetId = inventory.Items[9].AssetId;
        await using var scanScope = s.CreateScope();
        var service = scanScope.ServiceProvider.GetRequiredService<IInventoryService>();
        var db = scanScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await service.ScanAsync(inventory.Id, new(assetId.ToString(), "Serviceable", null), worker, Ct);
        Assert.False(result.Duplicate);
        Assert.Equal(assetId, Assert.Single(db.ChangeTracker.Entries<InventoryAssetSnapshot>()).Entity.AssetId);
        Assert.Equal(InventoryStatus.Active, await s.Db.Inventories.Where(x => x.Id == inventory.Id).Select(x => x.Status).SingleAsync(Ct));
        Assert.Equal(1, await s.Db.InventoryItems.CountAsync(x => x.InventoryId == inventory.Id && x.Result == ScanResult.Found, Ct));
    }

    [Fact]
    public async Task ReportServiceHidesUnassignedInventories()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await WorkerAsync(s, "1");
        var other = await WorkerAsync(s, "2");
        var inventory = await ActiveInventoryAsync(s, worker, 1);
        await s.Inventories.FinishScanningAsync(inventory.Id, worker, Ct);
        await s.Inventories.ConfirmAsync(inventory.Id, new("password-123"), worker, Ct);
        var reports = s.Services.GetRequiredService<IDocumentService>();
        await Assert.ThrowsAsync<NotFoundException>(() => reports.GetInventoryReportAsync(inventory.Id, "xlsx", other, Ct));
        Assert.NotEmpty((await reports.GetInventoryReportAsync(inventory.Id, "xlsx", worker, Ct)).Content);
        Assert.NotEmpty((await reports.GetInventoryReportAsync(inventory.Id, "xlsx", null, Ct)).Content);
    }

    [Fact]
    public async Task ReportEndpointEnforcesJwtAndInventoryAssignment()
    {
        await using var s = await fixture.OpenAsync();
        var adminTokens = await LoginAsync(s);
        var worker = await WorkerAsync(s, "1");
        await WorkerAsync(s, "2");
        var inventory = await ActiveInventoryAsync(s, worker, 1);
        await s.Inventories.FinishScanningAsync(inventory.Id, worker, Ct);
        await s.Inventories.ConfirmAsync(inventory.Id, new("password-123"), worker, Ct);
        var ownTokens = await s.Auth.LoginAsync(new("worker1", "password-123", "test"), Ct);
        var otherTokens = await s.Auth.LoginAsync(new("worker2", "password-123", "test"), Ct);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddInfrastructure(s.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseMiddleware<ApplicationExceptionMiddleware>();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapInventoryEndpoints();
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var path = $"/api/v1/inventories/{inventory.Id}/reports/inv-1.xlsx";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path, Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownTokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminTokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, Ct)).StatusCode);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task ConcurrentUnexpectedScansRecordEachCodeOncePerRound()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await WorkerAsync(s, "1");
        var active = await ActiveInventoryAsync(s, worker, 1);
        var fixtureAsset = await s.Db.Assets.AsNoTracking().SingleAsync(Ct);
        var outside = await s.Assets.CreateAsync(new("OUTSIDE", "Позднее ОС", "01", null, null, new DateOnly(2026, 1, 1), 100m,
            fixtureAsset.DepartmentId, fixtureAsset.LocationId, fixtureAsset.MateriallyResponsibleEmployeeId), Ct);
        async Task<ScanAssetResponse> ScanAsync(string raw, int round)
        {
            await using var scope = s.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IInventoryService>()
                .ScanAsync(active.Id, new(raw, "Serviceable", null), worker, Ct, round);
        }

        foreach (var (first, repeat, type) in new[]
        {
            ("наружный код", "  наружный код  ", "UnknownQr"),
            (outside.Id.ToString("D"), outside.Id.ToString("D").ToUpperInvariant(), "OutOfScope")
        })
        {
            var responses = await Task.WhenAll(ScanAsync(first, active.Round), ScanAsync(repeat, active.Round));
            Assert.All(responses, result => Assert.Equal(type, result.Result));
            Assert.Single(responses, result => result.Duplicate);
        }
        Assert.Equal(2, await s.Db.UnexpectedScans.AsNoTracking().CountAsync(Ct));
        Assert.Equal(2, await s.Db.AuditLogs.AsNoTracking().CountAsync(x => x.Action == "inventory.unexpected-scan", Ct));
        await s.Inventories.FinishScanningAsync(active.Id, worker, Ct);
        await s.Inventories.RejectAsync(active.Id, new("password-123", "Проверить ещё раз"), worker, Ct);
        var reopened = await s.Inventories.StartAsync(active.Id, TestSession.ActorId, Ct);
        Assert.False((await ScanAsync("наружный код", reopened.Round)).Duplicate);
        Assert.Equal(1, await s.Db.UnexpectedScans.AsNoTracking().CountAsync(Ct));
        Assert.Equal(3, await s.Db.AuditLogs.AsNoTracking().CountAsync(x => x.Action == "inventory.unexpected-scan", Ct));
    }

    [Fact]
    public async Task DelayedMobileCommandsCannotChangeAReopenedInventoryRound()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await WorkerAsync(s, "1");
        var first = await ActiveInventoryAsync(s, worker, 2);
        await s.Inventories.FinishScanningAsync(first.Id, worker, Ct);
        await s.Inventories.RejectAsync(first.Id, new("password-123", "Первый обход"), worker, Ct);
        var tokens = await s.Auth.LoginAsync(new("worker1", "password-123", "test"), Ct);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddInfrastructure(s.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseMiddleware<ApplicationExceptionMiddleware>();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapInventoryEndpoints();
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var path = $"/api/v1/mobile/inventories/{first.Id}";
        var scan = new ScanAssetRequest(first.Items[0].AssetId.ToString(), "Serviceable", null);
        var rejection = new RejectionRequest("password-123", "Первый обход");
        var confirmation = new ConfirmationRequest("password-123");

        await using (var restart = await s.Db.Database.BeginTransactionAsync(Ct))
        {
            await s.Inventories.StartAsync(first.Id, TestSession.ActorId, Ct);
            var queued = client.PostAsJsonAsync($"{path}/scan?expectedRound=1", scan, Ct);
            await WaitForLockWaitersAsync(s, "inventories", 1);
            await restart.CommitAsync(Ct);
            using var staleScan = await queued;
            Assert.Equal(HttpStatusCode.Conflict, staleScan.StatusCode);
        }
        using var staleFinish = await client.PostAsync($"{path}/finish-scanning?expectedRound=1&includeDetails=false", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, staleFinish.StatusCode);
        var current = await client.GetFromJsonAsync<JsonElement>($"{path}?page=1", Ct);
        Assert.Equal(2, current.GetProperty("round").GetInt32());
        Assert.Equal(2, current.GetProperty("counts").GetProperty("pending").GetInt32());

        using var accepted = await client.PostAsJsonAsync($"{path}/scan?expectedRound=2", scan, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var finish = await client.PostAsync($"{path}/finish-scanning?expectedRound=2&includeDetails=false", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, finish.StatusCode);
        using var staleReject = await client.PostAsJsonAsync($"{path}/reject?expectedRound=1&includeDetails=false", rejection, Ct);
        using var staleConfirm = await client.PostAsJsonAsync($"{path}/confirm?expectedRound=1&includeDetails=false", confirmation, Ct);
        Assert.Equal(HttpStatusCode.Conflict, staleReject.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleConfirm.StatusCode);
        Assert.False(await s.Db.GeneratedDocuments.AnyAsync(d => d.InventoryId == first.Id, Ct));
        using var confirmed = await client.PostAsJsonAsync($"{path}/confirm?expectedRound=2&includeDetails=false", confirmation, Ct);
        using var repeated = await client.PostAsJsonAsync($"{path}/confirm?expectedRound=2&includeDetails=false", confirmation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(2, await s.Db.GeneratedDocuments.CountAsync(d => d.InventoryId == first.Id, Ct));
        var entity = first.Id.ToString();
        Assert.Equal(1, await s.Db.AuditLogs.CountAsync(a => a.EntityId == entity && a.Action == "inventory.confirmed", Ct));
        Assert.Equal(1, await s.Db.AuditLogs.CountAsync(a => a.EntityId == entity && a.Action == "inventory.rejected", Ct));
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task FailedAutoStartCanBeRecoveredAndDraftSchedulesAssignAnEmployee()
    {
        await using var s = await fixture.OpenAsync();
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        await schedules.CreateAsync(new("Автозапуск", 12, DateTimeOffset.UtcNow.AddMinutes(-1), "AutoStart", true), Ct);
        async Task RunSchedulePassAsync()
        {
            await using var scope = s.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IInventoryJobProcessor>().ProcessSchedulesAsync(Ct);
        }
        await RunSchedulePassAsync();
        var attention = await s.Db.Inventories.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(InventoryStatus.NeedsAttention, attention.Status);
        Assert.Empty(await s.Db.InventoryItems.AsNoTracking().ToListAsync(Ct));
        var assignee = await WorkerAsync(s, "1");
        await AddAssetsAsync(s, 1);
        await s.Inventories.ReplaceAssigneeAsync(attention.Id, new(Guid.Empty, assignee), TestSession.ActorId, Ct);
        Assert.Equal("Active", (await s.Inventories.StartAsync(attention.Id, TestSession.ActorId, Ct)).Status);
        await schedules.CreateAsync(new("Черновик", 12, DateTimeOffset.UtcNow.AddMinutes(-1), "CreateDraft", true), Ct);
        await RunSchedulePassAsync();
        var draft = await s.Db.Inventories.AsNoTracking().Include(x => x.Assignments).SingleAsync(x => x.Status == InventoryStatus.Draft, Ct);
        Assert.Equal(assignee, Assert.Single(draft.Assignments).UserId);
    }

    private static async Task ReplayAsync(IAuthService auth, string token) =>
        await Assert.ThrowsAsync<UnauthorizedException>(() => auth.RefreshAsync(new(token, null), Ct));

    private static async Task<TokenResponse> LoginAsync(TestSession s)
    {
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", null), Ct);
        return await s.Auth.LoginAsync(new("admin", "password-123", "device"), Ct);
    }

    private static async Task<Guid> WorkerAsync(TestSession s, string suffix)
    {
        var dep = await s.Catalog.CreateDepartmentAsync(new("INV" + suffix, "Инвентаризаторы", true), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("INV" + suffix, "Исполнитель " + suffix, dep.Id, false, true), Ct);
        return (await s.Catalog.CreateUserAsync(new("worker" + suffix, "password-123", "InventoryEmployee", employee.Id), Ct)).Id;
    }

    private static async Task<InventoryDetailResponse> ActiveInventoryAsync(TestSession s, Guid worker, int count)
    {
        await AddAssetsAsync(s, count);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        var assigned = Assert.Single(draft.Assignments).UserId;
        if (assigned != worker) await s.Inventories.ReplaceAssigneeAsync(draft.Id, new(assigned, worker), TestSession.ActorId, Ct);
        return await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
    }

    private static async Task AddAssetsAsync(TestSession s, int count)
    {
        var dep = await s.Catalog.CreateDepartmentAsync(new("ASSETS", "Отдел", false), Ct);
        var location = await s.Catalog.CreateLocationAsync(new("L", "Кабинет", dep.Id), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("MOL", "МОЛ", dep.Id, true, true), Ct);
        for (var i = 0; i < count; i++) await s.Assets.CreateAsync(new("OS-" + i, "Стол", "01", null, 2025, new DateOnly(2026, 1, 1), 100m, dep.Id, location.Id, mol.Id), Ct);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task WaitForLockWaitersAsync(TestSession s, string table, int count)
    {
        await using var connection = new NpgsqlConnection(s.Db.Database.GetConnectionString());
        await connection.OpenAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE @table", connection);
            command.Parameters.AddWithValue("table", "%" + table + "%");
            if ((long)(await command.ExecuteScalarAsync(timeout.Token))! >= count) return;
            await Task.Delay(20, timeout.Token);
        }
    }
}
