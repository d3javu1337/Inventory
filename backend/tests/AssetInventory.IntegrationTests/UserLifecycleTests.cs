using AssetInventory.Application.Catalog;
using AssetInventory.Application.Common;
using AssetInventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class UserLifecycleTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConcurrentBlocksAlwaysLeaveOneAdministratorAndFailedBlockDoesNotChangeAccount()
    {
        await using var s = await fixture.OpenAsync();
        var a = await s.Catalog.CreateUserAsync(new("a", "password-123", "Administrator", null), Ct);
        var b = await s.Catalog.CreateUserAsync(new("b", "password-123", "Administrator", null), Ct);
        async Task<bool> Block(Guid id)
        {
            await using var scope = s.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<ICatalogService>().SetUserBlockedAsync(id, true, Ct); return true; }
            catch (ConflictException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Block(a.Id), Block(b.Id)), x => x);
        var users = await s.Db.Users.AsNoTracking().ToListAsync(Ct);
        Assert.Single(users, x => !x.IsBlocked);
        Assert.Equal(0, users.Single(x => !x.IsBlocked).SecurityVersion);
        Assert.Equal(1, users.Single(x => x.IsBlocked).SecurityVersion);
    }

    [Fact]
    public async Task DepartmentArchiveAndEmployeeTransferCannotTogetherDisableAdministratorAccess()
    {
        await using var s = await fixture.OpenAsync();
        var original = await s.Catalog.CreateDepartmentAsync(new("I", "Исходный отдел", true), Ct);
        var target = await s.Catalog.CreateDepartmentAsync(new("T", "Новый отдел", false), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("A", "Администратор", original.Id, false, true), Ct);
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", employee.Id), Ct);
        await using var barrier = await s.Db.Database.BeginTransactionAsync(Ct);
        await s.Db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(736412891)", Ct);
        async Task<bool> Change(bool archive)
        {
            await using var scope = s.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();
            try {
                if (archive) await catalog.ArchiveDepartmentAsync(target.Id, Ct);
                else await catalog.UpdateEmployeeAsync(employee.Id, new("A", "Администратор", target.Id, false, true), Ct);
                return true;
            }
            catch (ConflictException) { return false; }
            catch (NotFoundException) { return false; }
        }
        var archive = Change(true); var transfer = Change(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(s.Db.Database.GetConnectionString());
        await connection.OpenAsync(timeout.Token);
        while (true) {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%pg_advisory_xact_lock(736412891)%'", connection);
            if ((long)(await command.ExecuteScalarAsync(timeout.Token))! >= 2) break;
            await Task.Delay(20, timeout.Token);
        }
        await barrier.CommitAsync(Ct);
        Assert.Single(await Task.WhenAll(archive, transfer), x => x);
        Assert.True(await s.Db.Users.AsNoTracking().AnyAsync(x => !x.IsBlocked && x.Employee != null && x.Employee.IsEmployed && !x.Employee.Department.IsArchived, Ct));
    }

    [Fact]
    public async Task DismissalAndArchiveCannotRemoveLastAdministrator()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("I", "Инвентаризация", true), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("A", "Администратор", dep.Id, false, true), Ct);
        var account = await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", employee.Id), Ct);
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.UpdateEmployeeAsync(employee.Id, new("A", "Администратор", dep.Id, false, false), Ct));
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.ArchiveEmployeeAsync(employee.Id, Ct));
        var unchanged = await s.Db.Employees.AsNoTracking().SingleAsync(Ct);
        Assert.True(unchanged.IsEmployed); Assert.False(unchanged.IsArchived);
        Assert.False((await s.Db.Users.AsNoTracking().SingleAsync(Ct)).IsBlocked);
        await s.Catalog.CreateUserAsync(new("backup", "password-123", "Administrator", null), Ct);
        await s.Catalog.ArchiveEmployeeAsync(employee.Id, Ct);
        Assert.True((await s.Db.Users.AsNoTracking().SingleAsync(x => x.Id == account.Id, Ct)).IsBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingInventoryDepartmentEligibilityRevokesWorkerAndOldRefresh(bool transfer)
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("I", "Инвентаризация", true), Ct);
        var other = await s.Catalog.CreateDepartmentAsync(new("O", "Другой отдел", false), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("W", "Сотрудник", dep.Id, false, true), Ct);
        var user = await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", employee.Id), Ct);
        var tokens = await s.Auth.LoginAsync(new("worker", "password-123", null), Ct);
        if (transfer) await s.Catalog.UpdateEmployeeAsync(employee.Id, new("W", "Сотрудник", other.Id, false, true), Ct);
        else await s.Catalog.UpdateDepartmentAsync(dep.Id, new("I", "Инвентаризация", false), Ct);
        Assert.True((await s.Db.Users.AsNoTracking().SingleAsync(Ct)).IsBlocked);
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.RefreshAsync(new(tokens.RefreshToken, null), Ct));
        await Assert.ThrowsAsync<ConflictException>(() => s.Catalog.SetUserBlockedAsync(user.Id, false, Ct));
        if (transfer) await s.Catalog.UpdateEmployeeAsync(employee.Id, new("W", "Сотрудник", dep.Id, false, true), Ct);
        else await s.Catalog.UpdateDepartmentAsync(dep.Id, new("I", "Инвентаризация", true), Ct);
        await s.Catalog.SetUserBlockedAsync(user.Id, false, Ct);
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.RefreshAsync(new(tokens.RefreshToken, null), Ct));
        Assert.NotNull(await s.Auth.LoginAsync(new("worker", "password-123", null), Ct));
    }

    [Fact]
    public async Task OrganizationValidationRejectsMalformedValuesBeforeChangingTheExistingRecord()
    {
        await using var s = await fixture.OpenAsync();
        var valid = new AssetInventory.Contracts.Catalog.UpsertOrganizationRequest("Организация", "1234567890", "123456789", "12345678", "Адрес", "Директор");
        await s.Catalog.UpsertOrganizationAsync(valid, Ct);
        foreach (var invalid in new[] { valid with { Name = new string('N', 301) }, valid with { Inn = "abcdefghij" }, valid with { Kpp = "123" }, valid with { Okpo = "123" }, valid with { Address = new string('A', 501) } })
            await Assert.ThrowsAsync<RequestValidationException>(() => s.Catalog.UpsertOrganizationAsync(invalid, Ct));
        Assert.Equal(valid.Name, (await s.Db.Organizations.AsNoTracking().SingleAsync(Ct)).Name);
        Assert.Single(await s.Db.AuditLogs.Where(x => x.Action == "organization.created").ToListAsync(Ct));
    }

    [Fact]
    public async Task AdministratorCanBeLinkedToAnOrdinaryDepartmentButInventoryEmployeeCannot()
    {
        await using var s = await fixture.OpenAsync();
        var department = await s.Catalog.CreateDepartmentAsync(new("ADM", "Бухгалтерия", false), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("A", "Сотрудник бухгалтерии", department.Id, false, true), Ct);
        var inactive = await s.Catalog.CreateEmployeeAsync(new("B", "Уволенный сотрудник", department.Id, false, false), Ct);
        var created = await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", employee.Id), Ct);
        Assert.Equal(employee.Id, created.EmployeeId);
        Assert.Equal(created.Id, (await s.Auth.LoginAsync(new("admin", "password-123", null), Ct)).User.Id);
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", employee.Id), Ct));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            s.Catalog.CreateUserAsync(new("inactive", "password-123", "Administrator", inactive.Id), Ct));
    }

    [Fact]
    public async Task InvalidAccountsAndMalformedAuthenticationFailWithoutServerErrors()
    {
        await using var s = await fixture.OpenAsync();
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", null), Ct));
        foreach (var password in new[] { null, "        ", new string('a', 257) })
            await Assert.ThrowsAsync<RequestValidationException>(() => s.Catalog.CreateUserAsync(new("bad", password!, "Administrator", null), Ct));
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", null), Ct);
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.LoginAsync(new("admin", null!, null), Ct));
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.RefreshAsync(new(null!, null), Ct));
        await s.Auth.LogoutAsync(null!, Ct);
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Catalog.CreateDepartmentAsync(new(new string('D', 51), "Отдел", true), Ct));
        Assert.Single(await s.Db.Users.ToListAsync(Ct));
    }
}
