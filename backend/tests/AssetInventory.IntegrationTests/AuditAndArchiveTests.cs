using System.Security.Claims;
using System.Text.Json;
using AssetInventory.Application.Common;
using AssetInventory.Application.Auth;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Assets;
using AssetInventory.Contracts.Auth;
using AssetInventory.Contracts.Catalog;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class AuditAndArchiveTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CatalogChangesCaptureActorAndSafeBeforeAfterValues()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Первое", false), Ct);
        await s.Catalog.UpdateDepartmentAsync(dep.Id, new("D", "Второе", false), Ct);
        var log = await s.Db.AuditLogs.SingleAsync(x => x.Action == "department.updated", Ct);
        Assert.Equal(TestSession.ActorId, log.UserId);
        Assert.Equal("127.0.0.1", log.IpAddress);
        Assert.Equal("integration-correlation", log.CorrelationId);
        using var json = JsonDocument.Parse(log.DataJson!);
        var name = json.RootElement.GetProperty("changes").GetProperty("Name");
        Assert.Equal("Первое", name.GetProperty("before").GetString());
        Assert.Equal("Второе", name.GetProperty("after").GetString());
        var count = await s.Db.AuditLogs.CountAsync(Ct);
        await s.Db.SaveChangesAsync(Ct);
        Assert.Equal(count, await s.Db.AuditLogs.CountAsync(Ct));
        log.Action = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task PasswordsAndRefreshTokensNeverAppearInAudit()
    {
        await using var s = await fixture.OpenAsync();
        const string password = "private-password-987";
        var user = await s.Catalog.CreateUserAsync(new("admin", password, "Administrator", null), Ct);
        await s.Catalog.CreateUserAsync(new("backup-admin", password, "Administrator", null), Ct);
        var tokens = await s.Auth.LoginAsync(new("admin", password, "test-device"), Ct);
        await s.Catalog.SetUserBlockedAsync(user.Id, true, Ct);
        var logs = await s.Db.AuditLogs.ToListAsync(Ct);
        Assert.Contains(logs, x => x.Action == "user.created");
        Assert.Contains(logs, x => x.Action == "user.blocked");
        var serialized = JsonSerializer.Serialize(logs);
        Assert.DoesNotContain(password, serialized);
        Assert.DoesNotContain(tokens.RefreshToken, serialized);
        Assert.DoesNotContain("PasswordHash", serialized);
        Assert.DoesNotContain("TokenHash", serialized);
        Assert.DoesNotContain("NormalizedLogin", serialized);
    }

    [Fact]
    public async Task ReusingRotatedRefreshTokenRevokesItsReplacementChain()
    {
        await using var s = await fixture.OpenAsync();
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", null), Ct);
        var initial = await s.Auth.LoginAsync(new("admin", "password-123", "device"), Ct);
        var rotated = await s.Auth.RefreshAsync(new(initial.RefreshToken, "device"), Ct);

        var replay = await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.RefreshAsync(new(initial.RefreshToken, "device"), Ct));
        Assert.Contains("Цепочка сессии отозвана", replay.Message);
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.RefreshAsync(new(rotated.RefreshToken, "device"), Ct));
        Assert.True(await s.Db.RefreshTokens.AllAsync(x => x.RevokedAt != null, Ct));
    }

    [Fact]
    public async Task ConcurrentRefreshAllowsOneRotationAndRevokesTheWinningReplacement()
    {
        await using var s = await fixture.OpenAsync();
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", null), Ct);
        var initial = await s.Auth.LoginAsync(new("admin", "password-123", "device"), Ct);

        async Task<TokenResponse?> RotateAsync()
        {
            await using var scope = s.CreateScope();
            try { return await scope.ServiceProvider.GetRequiredService<IAuthService>().RefreshAsync(new(initial.RefreshToken, "device"), Ct); }
            catch (UnauthorizedException) { return null; }
        }

        var results = await Task.WhenAll(RotateAsync(), RotateAsync());
        var replacement = Assert.Single(results, x => x is not null)!;
        await using var verifyScope = s.CreateScope();
        await Assert.ThrowsAsync<UnauthorizedException>(() => verifyScope.ServiceProvider.GetRequiredService<IAuthService>()
            .RefreshAsync(new(replacement.RefreshToken, "device"), Ct));
    }

    [Fact]
    public async Task DepartmentRequiresArchivedChildrenAndRepeatedArchiveIsIdempotent()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Отдел", false), Ct);
        var room = await s.Catalog.CreateLocationAsync(new("L", "Кабинет", dep.Id), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("E", "Сотрудник", dep.Id, false, true), Ct);
        var auditCount = await s.Db.AuditLogs.CountAsync(Ct);
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.ArchiveDepartmentAsync(dep.Id, Ct));
        Assert.Equal(auditCount, await s.Db.AuditLogs.CountAsync(Ct));
        await s.Catalog.ArchiveLocationAsync(room.Id, Ct);
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.ArchiveDepartmentAsync(dep.Id, Ct));
        await s.Catalog.ArchiveEmployeeAsync(employee.Id, Ct);
        await s.Catalog.ArchiveDepartmentAsync(dep.Id, Ct);
        await s.Catalog.ArchiveDepartmentAsync(dep.Id, Ct);
        Assert.Equal(1, await s.Db.AuditLogs.CountAsync(x => x.Action == "department.archived", Ct));
        Assert.Empty(await s.Catalog.GetDepartmentsAsync(false, Ct));
        Assert.Single(await s.Catalog.GetDepartmentsAsync(true, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.UpdateDepartmentAsync(dep.Id, new("D", "Изменение", false), Ct));
    }

    [Fact]
    public async Task ArchivedEmployeeLosesLoginRefreshAndPreviouslyIssuedJwtAccess()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Инвентаризаторы", true), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("E", "Сотрудник", dep.Id, false, true), Ct);
        var user = await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", employee.Id), Ct);
        var tokens = await s.Auth.LoginAsync(new("worker", "password-123", "test"), Ct);
        Assert.Null((await ValidateJwt(s, user.Id)).Result?.Failure);
        await s.Catalog.ArchiveEmployeeAsync(employee.Id, Ct);
        Assert.True(await s.Db.Users.Where(x => x.Id == user.Id).Select(x => x.IsBlocked).SingleAsync(Ct));
        Assert.True(await s.Db.RefreshTokens.AllAsync(x => x.RevokedAt != null, Ct));
        Assert.NotNull((await ValidateJwt(s, user.Id)).Result?.Failure);
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.LoginAsync(new("worker", "password-123", "test"), Ct));
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.RefreshAsync(new(tokens.RefreshToken, "test"), Ct));
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.SetUserBlockedAsync(user.Id, false, Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Catalog.CreateUserAsync(new("another", "password-123", "InventoryEmployee", employee.Id), Ct));
    }

    [Fact]
    public async Task AssetArchivePreservesSnapshotsAndClosesAssignmentHistory()
    {
        await using var s = await fixture.OpenAsync();
        var asset = await CreateAsset(s);
        var inventory = new Inventory { Number = "INV-HISTORY", Type = InventoryType.Planned, Reason = InventoryReason.AnnualReporting, PlannedAt = DateTimeOffset.UtcNow, Status = InventoryStatus.Completed };
        inventory.Items.Add(new InventoryAssetSnapshot { AssetId = asset.Id, InventoryNumber = asset.InventoryNumber, Name = asset.Name, AccountingAccount = asset.AccountingAccount,
            AcceptedAt = asset.AcceptedAt, InitialCost = asset.InitialCost, DepartmentName = "Отдел", LocationName = "Кабинет", MateriallyResponsibleFullName = "МОЛ", Result = ScanResult.Found });
        s.Db.Add(inventory);
        await s.Db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.ArchiveLocationAsync(asset.LocationId, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.ArchiveEmployeeAsync(asset.MateriallyResponsibleEmployeeId, Ct));
        await s.Assets.ArchiveAsync(asset.Id, Ct);
        await s.Assets.ArchiveAsync(asset.Id, Ct);
        Assert.Equal(1, await s.Db.AuditLogs.CountAsync(x => x.Action == "asset.archived", Ct));
        Assert.Empty((await s.Assets.GetAsync(new(null, null, null, false), Ct)).Items);
        Assert.Single((await s.Assets.GetAsync(new(null, null, null, true), Ct)).Items);
        Assert.NotNull((await s.Assets.GetHistoryAsync(asset.Id, Ct)).Single().ValidTo);
        var snapshot = await s.Db.InventoryItems.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(asset.Name, snapshot.Name);
        Assert.Equal(asset.InitialCost, snapshot.InitialCost);
        await Assert.ThrowsAsync<ConflictException>(() => s.Assets.UpdateAsync(asset.Id, 1, ToRequest(asset), Ct));
    }

    [Fact]
    public async Task ActiveAssetsPreventMovingTheirLocationOrRemovingMolStatus()
    {
        await using var s = await fixture.OpenAsync();
        var asset = await CreateAsset(s);
        var second = await s.Catalog.CreateDepartmentAsync(new("D2", "Другой отдел", false), Ct);
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.UpdateLocationAsync(asset.LocationId, new("L", "Кабинет", second.Id), Ct));
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.UpdateEmployeeAsync(asset.MateriallyResponsibleEmployeeId, new("E", "МОЛ", asset.DepartmentId, false, true), Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EitherLegacyArchiveMarkerAllowsChangingCatalogReferences(bool statusOnly)
    {
        await using var s = await fixture.OpenAsync();
        var asset = await CreateAsset(s);
        var record = await s.Db.Assets.SingleAsync(Ct);
        if (statusOnly) record.Status = AssetStatus.Archived;
        else record.ArchivedAt = DateTimeOffset.UtcNow;
        await s.Db.SaveChangesAsync(Ct);
        var archived = await s.Assets.GetByIdAsync(asset.Id, Ct);
        var history = Assert.Single(await s.Assets.GetHistoryAsync(asset.Id, Ct));
        Assert.Empty((await s.Assets.GetAsync(new(null, null, null, false), Ct)).Items);

        var other = await s.Catalog.CreateDepartmentAsync(new("OTHER", "Другой отдел", false), Ct);
        await s.Catalog.UpdateLocationAsync(asset.LocationId, new("L", "Кабинет", other.Id), Ct);
        await s.Catalog.UpdateEmployeeAsync(asset.MateriallyResponsibleEmployeeId, new("E", "МОЛ", asset.DepartmentId, false, true), Ct);
        await s.Catalog.ArchiveLocationAsync(asset.LocationId, Ct);
        await s.Catalog.ArchiveEmployeeAsync(asset.MateriallyResponsibleEmployeeId, Ct);
        await s.Catalog.ArchiveDepartmentAsync(asset.DepartmentId, Ct);

        Assert.Equal(archived, await s.Assets.GetByIdAsync(asset.Id, Ct));
        Assert.Equal(history, Assert.Single(await s.Assets.GetHistoryAsync(asset.Id, Ct)));
        Assert.True(await s.Db.Departments.AnyAsync(x => x.Id == asset.DepartmentId && x.IsArchived, Ct));
        Assert.True(await s.Db.Locations.AnyAsync(x => x.Id == asset.LocationId && x.DepartmentId == other.Id && x.IsArchived, Ct));
        Assert.True(await s.Db.Employees.AnyAsync(x => x.Id == asset.MateriallyResponsibleEmployeeId && x.IsArchived && !x.IsMateriallyResponsible, Ct));
    }

    [Fact]
    public async Task DraftCannotStartWithAnUnavailablePreviouslyAssignedEmployee()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Инвентаризаторы", true), Ct);
        var inactiveEmployee = await s.Catalog.CreateEmployeeAsync(new("E1", "Недоступный", dep.Id, false, true), Ct);
        var availableEmployee = await s.Catalog.CreateEmployeeAsync(new("E2", "Доступный", dep.Id, false, true), Ct);
        var inactiveUser = await s.Catalog.CreateUserAsync(new("inactive", "password-123", "InventoryEmployee", inactiveEmployee.Id), Ct);
        await s.Catalog.CreateUserAsync(new("available", "password-123", "InventoryEmployee", availableEmployee.Id), Ct);
        var asset = await CreateAsset(s, "A");
        var inventory = new Inventory { Number = "INV-ASSIGNEE", Type = InventoryType.Planned, Reason = InventoryReason.AnnualReporting, PlannedAt = DateTimeOffset.UtcNow };
        inventory.Assignments.Add(new InventoryAssignment { UserId = inactiveUser.Id, Role = CommissionRole.Chairperson });
        s.Db.Inventories.Add(inventory);
        await s.Db.SaveChangesAsync(Ct);
        await s.Catalog.SetUserBlockedAsync(inactiveUser.Id, true, Ct);

        var error = await Assert.ThrowsAsync<ConflictException>(() => s.Inventories.StartAsync(inventory.Id, TestSession.ActorId, Ct));
        Assert.Contains("Назначенный исполнитель недоступен", error.Message);
        Assert.Equal(InventoryStatus.Draft, await s.Db.Inventories.Where(x => x.Id == inventory.Id).Select(x => x.Status).SingleAsync(Ct));
        Assert.Empty(await s.Db.InventoryItems.Where(x => x.InventoryId == inventory.Id).ToListAsync(Ct));
        Assert.NotNull(asset);
    }

    [Fact]
    public async Task FailedDatabaseWriteRollsBackAuditAndRetryDoesNotDuplicateIt()
    {
        await using var s = await fixture.OpenAsync();
        await s.Catalog.CreateDepartmentAsync(new("D", "Первое", false), Ct);
        var duplicate = new Department { Code = "D", Name = "Второе" };
        s.Db.Departments.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync(Ct));
        Assert.Equal(1, await s.Db.Departments.AsNoTracking().CountAsync(Ct));
        Assert.Equal(1, await s.Db.AuditLogs.AsNoTracking().CountAsync(Ct));
        duplicate.Code = "D2";
        await s.Db.SaveChangesAsync(Ct);
        Assert.Equal(2, await s.Db.AuditLogs.CountAsync(Ct));
    }

    [Fact]
    public async Task AuditPaginationAndDeletedEntityDisplayNameRemainCorrect()
    {
        await using var s = await fixture.OpenAsync();
        var schedules = s.Services.GetRequiredService<IScheduleService>();
        var schedule = await schedules.CreateAsync(new("Годовая", 12, DateTimeOffset.UtcNow, "CreateDraft", true), Ct);
        await schedules.DeleteAsync(schedule.Id, Ct);
        await s.Catalog.CreateDepartmentAsync(new("D", "Отдел", false), Ct);
        var page = await s.Monitoring.GetAuditAsync(2, 1, Ct);
        Assert.Equal(2, page.Page);
        Assert.Equal(1, page.PageSize);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal("schedule.deleted", page.Items.Single().Action);
        Assert.Equal("Годовая", page.Items.Single().EntityDisplayName);
    }

    private static async Task<TokenValidatedContext> ValidateJwt(TestSession s, Guid userId)
    {
        var options = s.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var context = new TokenValidatedContext(s.Http, new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)), options)
        { Principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")) };
        await options.Events.TokenValidated(context);
        return context;
    }

    private static async Task<AssetResponse> CreateAsset(TestSession s, string suffix = "")
    {
        var dep = await s.Catalog.CreateDepartmentAsync(new($"D{suffix}", "Отдел", false), Ct);
        var room = await s.Catalog.CreateLocationAsync(new($"L{suffix}", "Кабинет", dep.Id), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new($"E{suffix}", "МОЛ", dep.Id, true, true), Ct);
        return await s.Assets.CreateAsync(new($"OS-1{suffix}", "Стол", "01", null, 2025, new DateOnly(2026, 1, 1), 1234.56m, dep.Id, room.Id, employee.Id), Ct);
    }

    private static UpsertAssetRequest ToRequest(AssetResponse a) => new(a.InventoryNumber, a.Name, a.AccountingAccount, a.SerialNumber, a.ManufactureYear, a.AcceptedAt, a.InitialCost, a.DepartmentId, a.LocationId, a.MateriallyResponsibleEmployeeId);
}
