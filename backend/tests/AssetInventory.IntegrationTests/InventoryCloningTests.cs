using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AssetInventory.Api.Endpoints;
using AssetInventory.Api.Middleware;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class InventoryCloningTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Guid Worker, Guid Mol, Guid Asset)> SeedAsync(TestSession s)
    {
        var department = await s.Catalog.CreateDepartmentAsync(new("D", "Имущество", false), Ct);
        var location = await s.Catalog.CreateLocationAsync(new("L", "Кабинет", department.Id), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("M", "МОЛ", department.Id, true, true), Ct);
        var workers = await s.Catalog.CreateDepartmentAsync(new("W", "Инвентаризация", true), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("I", "Инвентаризатор", workers.Id, false, true), Ct);
        var worker = await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", employee.Id), Ct);
        var asset = await s.Assets.CreateAsync(new("OS-1", "Старое название", "01", null, 2025, new(2026, 1, 1), 100m, department.Id, location.Id, mol.Id), Ct);
        return (worker.Id, mol.Id, asset.Id);
    }

    [Fact]
    public async Task CompletedCloneUsesFreshSnapshotAndDoesNotChangeOriginalReports()
    {
        await using var s = await fixture.OpenAsync();
        var (worker, _, assetId) = await SeedAsync(s);
        var original = await s.Inventories.CreateAsync(new("Manual", "Other", "Уточнение учета", null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        await s.Inventories.StartAsync(original.Id, TestSession.ActorId, Ct);
        await s.Inventories.ScanAsync(original.Id, new("unknown", "Serviceable", null), worker, Ct);
        await s.Inventories.ScanAsync(original.Id, new(assetId.ToString(), "Damaged", "Повреждено"), worker, Ct);
        await s.Inventories.ConfirmAsync(original.Id, new("password-123"), worker, Ct);
        s.Db.ChangeTracker.Clear();
        var completed = await s.Inventories.GetByIdAsync(original.Id, null, Ct);
        var documents = s.Services.GetRequiredService<IDocumentService>();
        var pdf = await documents.GetInventoryReportAsync(original.Id, "pdf", worker, Ct);
        var frozen = (await s.Db.Inventories.AsNoTracking().SingleAsync(x => x.Id == original.Id, Ct)).ReportContextJson;
        var asset = await s.Db.Assets.SingleAsync(x => x.Id == assetId, Ct); asset.Name = "Новое название";
        await s.Db.SaveChangesAsync(Ct);
        var planned = DateTimeOffset.UtcNow.AddDays(1);
        var clone = await s.Inventories.CloneAsync(original.Id, new(planned), TestSession.ActorId, Ct);
        Assert.NotEqual(original.Id, clone.Id); Assert.NotEqual(original.Number, clone.Number);
        Assert.Equal("Draft", clone.Status); Assert.Equal(planned, clone.PlannedAt);
        Assert.Equal(original.Id, clone.SourceInventoryId); Assert.Equal(original.Number, clone.SourceInventoryNumber);
        Assert.Equal("Уточнение учета", clone.ReasonDetails);
        Assert.Empty(clone.Items); Assert.Empty(clone.UnexpectedScans); Assert.Null(clone.StartedAt); Assert.Null(clone.CompletedAt);
        Assert.Equal("Pending", Assert.Single(clone.Assignments).ConfirmationStatus);
        var cloneEntity = await s.Db.Inventories.AsNoTracking().SingleAsync(x => x.Id == clone.Id, Ct);
        Assert.Null(cloneEntity.ReportContextJson); Assert.Null(cloneEntity.SourceScheduleId); Assert.Null(cloneEntity.ScheduledPeriod);
        var active = await s.Inventories.StartAsync(clone.Id, TestSession.ActorId, Ct);
        Assert.Equal("Новое название", Assert.Single(active.Items).Name); Assert.Equal("Pending", active.Items[0].Result);
        var unchanged = await s.Inventories.GetByIdAsync(original.Id, null, Ct);
        Assert.Equal(completed.Status, unchanged.Status); Assert.Equal(completed.CompletedAt, unchanged.CompletedAt);
        Assert.Equal(completed.Items, unchanged.Items); Assert.Equal(completed.Assignments, unchanged.Assignments);
        Assert.Equal(completed.UnexpectedScans, unchanged.UnexpectedScans);
        Assert.Equal(frozen, (await s.Db.Inventories.AsNoTracking().SingleAsync(x => x.Id == original.Id, Ct)).ReportContextJson);
        Assert.Equal(pdf.Content, (await documents.GetInventoryReportAsync(original.Id, "pdf", worker, Ct)).Content);
        Assert.Equal(2, await s.Db.GeneratedDocuments.CountAsync(Ct));
        var audit = await s.Db.AuditLogs.SingleAsync(x => x.Action == "inventory.cloned", Ct);
        Assert.Equal(clone.Id.ToString(), audit.EntityId); Assert.Contains(original.Id.ToString(), audit.DataJson!);
    }

    [Fact]
    public async Task CancelledEventCloneKeepsMolScopeButNotScheduleOwnershipOrBlockedAssignee()
    {
        await using var s = await fixture.OpenAsync();
        var (worker, mol, _) = await SeedAsync(s);
        var source = await s.Inventories.CreateAsync(new("EventDriven", "MolChange", null, mol, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        var entity = await s.Db.Inventories.SingleAsync(x => x.Id == source.Id, Ct);
        entity.SourceScheduleId = Guid.CreateVersion7(); entity.ScheduledPeriod = new(2026, 10, 1);
        await s.Db.SaveChangesAsync(Ct);
        await s.Inventories.CancelAsync(source.Id, TestSession.ActorId, Ct);
        await s.Catalog.SetUserBlockedAsync(worker, true, Ct);
        var clone = await s.Inventories.CloneAsync(source.Id, new(DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        Assert.Equal(mol, clone.TargetEmployeeId); Assert.Equal("EventDriven", clone.Type); Assert.Empty(clone.Assignments);
        var clonedEntity = await s.Db.Inventories.AsNoTracking().SingleAsync(x => x.Id == clone.Id, Ct);
        Assert.Null(clonedEntity.SourceScheduleId); Assert.Null(clonedEntity.ScheduledPeriod);
        await Assert.ThrowsAsync<ConflictException>(() => s.Inventories.StartAsync(clone.Id, TestSession.ActorId, Ct));
    }

    [Fact]
    public async Task CreateAndCloneAcceptOffsetDatesAndPersistTheirUtcInstants()
    {
        await using var s = await fixture.OpenAsync();
        var local = new DateTimeOffset(2026, 11, 1, 0, 15, 0, TimeSpan.FromHours(5));
        var actor = TestSession.ActorId;
        var source = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, local), actor, Ct);
        Assert.True(source.PlannedAt.EqualsExact(local.ToUniversalTime()));
        await s.Inventories.CancelAsync(source.Id, actor, Ct);

        var next = local.AddDays(1);
        var clone = await s.Inventories.CloneAsync(source.Id, new(next), actor, Ct);
        Assert.True(clone.PlannedAt.EqualsExact(next.ToUniversalTime()));
        Assert.Equal(source.Id, clone.SourceInventoryId);
    }

    [Theory]
    [InlineData(InventoryStatus.Draft)]
    [InlineData(InventoryStatus.Active)]
    [InlineData(InventoryStatus.AwaitingConfirmation)]
    [InlineData(InventoryStatus.ReviewRequired)]
    [InlineData(InventoryStatus.NeedsAttention)]
    public async Task NonTerminalStatusesCannotBeCloned(InventoryStatus status)
    {
        await using var s = await fixture.OpenAsync();
        var source = new Inventory { Number = "SOURCE", Type = InventoryType.Manual, Status = status, PlannedAt = DateTimeOffset.UtcNow };
        s.Db.Inventories.Add(source); await s.Db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<ConflictException>(() => s.Inventories.CloneAsync(source.Id, new(DateTimeOffset.UtcNow), TestSession.ActorId, Ct));
        Assert.Equal(1, await s.Db.Inventories.CountAsync(Ct));
    }

    [Fact]
    public async Task MissingSourceAndEmptyDateAreRejectedAndSqlFailureRollsBackCloneAndAudit()
    {
        await using var s = await fixture.OpenAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => s.Inventories.CloneAsync(Guid.CreateVersion7(), new(DateTimeOffset.UtcNow), TestSession.ActorId, Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.CloneAsync(Guid.CreateVersion7(), new(default), TestSession.ActorId, Ct));
        var source = new Inventory { Number = "SOURCE", Status = InventoryStatus.Cancelled, Type = InventoryType.Manual, PlannedAt = DateTimeOffset.UtcNow };
        s.Db.Inventories.Add(source); await s.Db.SaveChangesAsync(Ct);
        await s.Db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_clone_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.action = 'inventory.cloned' THEN RAISE EXCEPTION 'test rollback'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_clone_audit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_clone_audit();", Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Inventories.CloneAsync(source.Id, new(DateTimeOffset.UtcNow), TestSession.ActorId, Ct));
        Assert.Equal(1, await s.Db.Inventories.AsNoTracking().CountAsync(Ct));
        Assert.False(await s.Db.AuditLogs.AsNoTracking().AnyAsync(x => x.Action == "inventory.cloned", Ct));
    }

    [Fact]
    public async Task HttpCloneRequiresAdministratorAndReturnsCreatedLocation()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", null), Ct);
        var admin = await s.Auth.LoginAsync(new("admin", "password-123", "test"), Ct);
        var worker = await s.Auth.LoginAsync(new("worker", "password-123", "test"), Ct);
        var source = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), admin.User.Id, Ct);
        await s.Inventories.CancelAsync(source.Id, admin.User.Id, Ct);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddInfrastructure(s.Configuration); builder.Services.RemoveAll<IHostedService>(); builder.Services.AddProblemDetails();
        await using var app = builder.Build(); app.Urls.Add("http://127.0.0.1:0");
        app.UseMiddleware<ApplicationExceptionMiddleware>(); app.UseAuthentication(); app.UseAuthorization(); app.MapInventoryEndpoints();
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var path = $"/api/v1/inventories/{source.Id}/clone"; var request = new CloneInventoryRequest(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, request, Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", worker.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, request, Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
        var response = await client.PostAsJsonAsync(path, request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var clone = await response.Content.ReadFromJsonAsync<InventoryDetailResponse>(Ct);
        Assert.Equal($"/api/v1/inventories/{clone!.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal(source.Id, clone.SourceInventoryId);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task DirectRequestsRejectUndefinedEnumsAndInvalidFieldsBeforeWriting()
    {
        await using var s = await fixture.OpenAsync();
        var (worker, _, asset) = await SeedAsync(s);
        var valid = new CreateInventoryRequest("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow);
        foreach (var invalid in new[] { valid with { Type = "999" }, valid with { Reason = "999" }, valid with { PlannedAt = default }, valid with { ReasonDetails = new string('x', 1001) }, valid with { Type = "EventDriven", TargetEmployeeId = Guid.Empty } })
            await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.CreateAsync(invalid, TestSession.ActorId, Ct));
        Assert.Empty(await s.Db.Inventories.ToListAsync(Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Catalog.CreateUserAsync(new("invalid-role", "password-123", "999", null), Ct));
        var draft = await s.Inventories.CreateAsync(valid, TestSession.ActorId, Ct);
        await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        foreach (var invalid in new[] { new ScanAssetRequest(null!, "Serviceable", null), new(" ", "Serviceable", null), new(new string('x', 501), "Serviceable", null), new(asset.ToString(), "999", null), new(asset.ToString(), "Serviceable", new string('x', 2001)), new("bad\0qr", "Serviceable", null), new(asset.ToString(), "Serviceable", "bad\0comment") })
            await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.ScanAsync(draft.Id, invalid, worker, Ct));
        Assert.Empty(await s.Db.UnexpectedScans.ToListAsync(Ct));
        Assert.Equal("Pending", Assert.Single((await s.Inventories.GetByIdAsync(draft.Id, null, Ct)).Items).Result);
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Inventories.RejectAsync(draft.Id, new("password-123", new string('x', 1001)), worker, Ct));
    }

    [Fact]
    public async Task MolEventDetailsSurviveProcessingAndIdempotentReplay()
    {
        await using var s = await fixture.OpenAsync();
        var (_, mol, _) = await SeedAsync(s);
        var events = s.Services.GetRequiredService<IIntegrationEventService>();
        var request = new MolChangeEventRequest("details-test", mol, DateTimeOffset.UtcNow, "  Передача имущества при смене МОЛ  ");
        var received = await events.RegisterMolChangeAsync(request, Ct);
        Assert.Equal(received.Id, (await events.RegisterMolChangeAsync(request with { Details = "Другое описание" }, Ct)).Id);
        var payload = (await s.Db.IntegrationEvents.AsNoTracking().SingleAsync(Ct)).PayloadJson;
        Assert.Contains("Details", payload); Assert.DoesNotContain("Другое описание", payload);
        await s.Services.GetRequiredService<IInventoryJobProcessor>().ProcessEventsAsync(Ct);
        var processed = Assert.Single(await events.GetAsync(Ct));
        var inventory = await s.Inventories.GetByIdAsync(processed.CreatedInventoryId!.Value, null, Ct);
        Assert.Contains("Передача имущества при смене МОЛ", inventory.ReasonDetails!);
        await Assert.ThrowsAsync<RequestValidationException>(() => events.RegisterMolChangeAsync(request with { EventId = "bad", Details = new string('x', 1001) }, Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => events.RegisterDismissalAsync(new("bad-date", mol, default), Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => events.RegisterDismissalAsync(new("bad-employee", Guid.Empty, DateTimeOffset.UtcNow), Ct));
        Assert.Equal(1, await s.Db.IntegrationEvents.CountAsync(Ct));
    }
}
