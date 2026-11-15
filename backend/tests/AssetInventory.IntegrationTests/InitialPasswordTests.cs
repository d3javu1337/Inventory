using AssetInventory.Application.Auth;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Auth;
using AssetInventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class InitialPasswordTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<UserAccount> SeedAsync(TestSession s)
    {
        var user = new UserAccount { Login = "first", NormalizedLogin = "FIRST", PasswordHash = "", Role = UserRole.Administrator };
        s.Db.Users.Add(user); await s.Db.SaveChangesAsync(Ct);
        return user;
    }

    [Fact]
    public async Task FailedSessionInsertRollsBackInitialPasswordAndAllowsRetry()
    {
        await using var s = await fixture.OpenAsync();
        var user = await SeedAsync(s);
        await s.Db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_initial_session() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'session insert failed'; END $$; CREATE TRIGGER fail_initial_session BEFORE INSERT ON refresh_tokens FOR EACH ROW EXECUTE FUNCTION fail_initial_session();", Ct);
        await using (var failed = s.CreateScope())
            await Assert.ThrowsAsync<DbUpdateException>(() => failed.ServiceProvider.GetRequiredService<IAuthService>().SetInitialPasswordAsync(new("first", "first-password", "test"), Ct));
        Assert.Equal("", (await s.Db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, Ct)).PasswordHash);
        Assert.Empty(await s.Db.RefreshTokens.AsNoTracking().ToListAsync(Ct));
        await s.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_initial_session ON refresh_tokens; DROP FUNCTION fail_initial_session();", Ct);
        await using var retry = s.CreateScope();
        var auth = retry.ServiceProvider.GetRequiredService<IAuthService>();
        Assert.True((await auth.StartLoginAsync(new("first"), Ct)).PasswordSetupRequired);
        var pair = await auth.SetInitialPasswordAsync(new("first", "retry-password", "test"), Ct);
        Assert.Equal(user.Id, pair.User.Id);
        Assert.False((await auth.StartLoginAsync(new("first"), Ct)).PasswordSetupRequired);
        Assert.NotNull(await auth.LoginAsync(new("first", "retry-password", "test"), Ct));
    }

    [Fact]
    public async Task BlockingDuringInitialPasswordRequestPreventsPasswordAndTokenIssuance()
    {
        await using var s = await fixture.OpenAsync();
        var user = await SeedAsync(s);
        await s.Catalog.CreateUserAsync(new("backup", "backup-password", "Administrator", null), Ct);
        await using var barrier = await s.Db.Database.BeginTransactionAsync(Ct);
        await s.Catalog.SetUserBlockedAsync(user.Id, true, Ct);
        await using var scope = s.CreateScope();
        var pending = scope.ServiceProvider.GetRequiredService<IAuthService>().SetInitialPasswordAsync(new("first", "first-password", "test"), Ct);
        await WaitForPasswordUpdatesAsync(s, 1);
        await barrier.CommitAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedException>(() => pending);
        var unchanged = await s.Db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, Ct);
        Assert.True(unchanged.IsBlocked); Assert.Equal("", unchanged.PasswordHash);
        Assert.Empty(await s.Db.RefreshTokens.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentInitialPasswordsCreateOneSessionAndOnlyTheWinningPasswordWorks()
    {
        await using var s = await fixture.OpenAsync();
        var user = await SeedAsync(s);
        await using var barrier = await s.Db.Database.BeginTransactionAsync(Ct);
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM user_accounts WHERE id = {user.Id} FOR UPDATE", Ct);
        async Task<string?> Set(string password)
        {
            await using var scope = s.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<IAuthService>().SetInitialPasswordAsync(new("first", password, "test"), Ct); return password; }
            catch (ConflictException) { return null; }
        }
        var first = Set("first-password"); var second = Set("second-password");
        await WaitForPasswordUpdatesAsync(s, 2);
        await barrier.CommitAsync(Ct);
        var winner = Assert.Single(await Task.WhenAll(first, second), x => x != null)!;
        Assert.Single(await s.Db.RefreshTokens.AsNoTracking().ToListAsync(Ct));
        await using var verify = s.CreateScope();
        var auth = verify.ServiceProvider.GetRequiredService<IAuthService>();
        Assert.NotNull(await auth.LoginAsync(new("first", winner, "verify"), Ct));
        var loser = winner == "first-password" ? "second-password" : "first-password";
        await Assert.ThrowsAsync<UnauthorizedException>(() => auth.LoginAsync(new("first", loser, "verify"), Ct));
    }

    private static async Task WaitForPasswordUpdatesAsync(TestSession s, int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(s.Db.Database.GetConnectionString());
        await connection.OpenAsync(timeout.Token);
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%UPDATE user_accounts%'", connection);
            if ((long)(await command.ExecuteScalarAsync(timeout.Token))! >= count) break;
            await Task.Delay(20, timeout.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongDeviceMetadataDoesNotBreakAnySessionIssuancePath(bool unicodeBoundary)
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        var device = unicodeBoundary ? new string('a', 299) + "😀end" : "  " + new string('a', 500) + "  ";
        var expected = new string('a', unicodeBoundary ? 299 : 300);
        var first = await s.Auth.SetInitialPasswordAsync(new("first", "first-password", device), Ct);
        await s.Auth.LoginAsync(new("first", "first-password", device), Ct);
        await s.Auth.RefreshAsync(new(first.RefreshToken, device), Ct);
        await s.Auth.ChangePasswordAsync(first.User.Id, new("first-password", "changed-password", device), Ct);
        var rows = await s.Db.RefreshTokens.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal(expected, row.Device));
    }
}
