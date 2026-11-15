using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AssetInventory.Api.Endpoints;
using AssetInventory.Api.Middleware;
using AssetInventory.Application.Auth;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Auth;
using AssetInventory.Infrastructure;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class PasswordChangeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PasswordChangeRotatesCurrentSessionAndRejectsOldJwtAndRefreshOnEveryDevice()
    {
        await using var s = await fixture.OpenAsync();
        var owner = await s.Catalog.CreateUserAsync(new("owner", "old-password", "Administrator", null), Ct);
        await s.Catalog.CreateUserAsync(new("other", "other-password", "Administrator", null), Ct);
        var initial = await s.Auth.LoginAsync(new("owner", "old-password", "device-1"), Ct);
        var otherDevice = await s.Auth.LoginAsync(new("owner", "old-password", "device-2"), Ct);
        var unrelated = await s.Auth.LoginAsync(new("other", "other-password", "other"), Ct);
        // Version-zero accounts keep their pre-migration JWTs until a password change.
        var old = new JwtSecurityTokenHandler().ReadJwtToken(initial.AccessToken);
        var legacy = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(old.Issuer, old.Audiences.Single(), old.Claims.Where(c => c.Type is not "sv" and not "aud" and not "iss" and not "exp"), expires: old.ValidTo,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(s.Configuration["Jwt:SigningKey"]!)), SecurityAlgorithms.HmacSha256)));
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddConfiguration(s.Configuration);
        builder.Logging.ClearProviders();
        builder.Services.AddInfrastructure(s.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseMiddleware<ApplicationExceptionMiddleware>();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapAuthEndpoints();
        app.MapGet("/probe", () => "OK").RequireAuthorization();
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        async Task<HttpStatusCode> ProbeAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(legacy));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", initial.AccessToken);
        using var wrong = await client.PostAsJsonAsync("/api/v1/auth/mobile/change-password", new ChangePasswordRequest("wrong", "new-password", "device-1"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(initial.AccessToken));
        using var response = await client.PostAsJsonAsync("/api/v1/auth/mobile/change-password", new ChangePasswordRequest("old-password", "new-password", "device-1"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(changed.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(unrelated.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(initial.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(otherDevice.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(legacy));
        await using var verification = s.CreateScope();
        var auth = verification.ServiceProvider.GetRequiredService<IAuthService>();
        await Assert.ThrowsAsync<UnauthorizedException>(() => auth.RefreshAsync(new(initial.RefreshToken, null), Ct));
        await Assert.ThrowsAsync<UnauthorizedException>(() => auth.RefreshAsync(new(otherDevice.RefreshToken, null), Ct));
        Assert.NotNull(await auth.RefreshAsync(new(changed.RefreshToken, null), Ct));
        await Assert.ThrowsAsync<UnauthorizedException>(() => auth.LoginAsync(new("owner", "old-password", null), Ct));
        Assert.NotNull(await auth.LoginAsync(new("owner", "new-password", null), Ct));
        var audit = await s.Db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "user.password-changed", Ct);
        Assert.Equal(owner.Id, audit.UserId);
        Assert.Null(audit.DataJson);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.AccessToken);
        using var webResponse = await client.PostAsJsonAsync("/api/v1/auth/web/change-password", new { currentPassword = "new-password", newPassword = "newer-password" }, Ct);
        Assert.Equal(HttpStatusCode.OK, webResponse.StatusCode);
        Assert.Contains(webResponse.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("path=/api/v1/auth/web", StringComparison.OrdinalIgnoreCase));
        using var webBody = System.Text.Json.JsonDocument.Parse(await webResponse.Content.ReadAsStringAsync(Ct));
        Assert.False(webBody.RootElement.TryGetProperty("refreshToken", out _));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(webBody.RootElement.GetProperty("accessToken").GetString()!));
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(changed.AccessToken));
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task StaleWebAccountCannotRotateRevokeOrReplaceAnotherAccountsCookie()
    {
        await using var s = await fixture.OpenAsync();
        var first = await s.Catalog.CreateUserAsync(new("first", "first-password", "Administrator", null), Ct);
        var second = await s.Catalog.CreateUserAsync(new("second", "second-password", "Administrator", null), Ct);
        var firstSession = await s.Auth.LoginAsync(new("first", "first-password", "web"), Ct);
        var secondSession = await s.Auth.LoginAsync(new("second", "second-password", "web"), Ct);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddConfiguration(s.Configuration);
        builder.Logging.ClearProviders();
        builder.Services.AddInfrastructure(s.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseMiddleware<ApplicationExceptionMiddleware>();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapAuthEndpoints();
        await app.StartAsync(Ct);
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.Single()) };

        async Task<HttpResponseMessage> SendAsync(string action, Guid expectedUser, string? bearer = null, HttpContent? body = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/web/" + action) { Content = body };
            request.Headers.Add("Cookie", "asset_inventory_refresh=" + secondSession.RefreshToken);
            request.Headers.Add("X-Expected-User-Id", expectedUser.ToString());
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return await client.SendAsync(request, Ct);
        }

        using var staleRefresh = await SendAsync("refresh", first.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, staleRefresh.StatusCode);
        Assert.False(staleRefresh.Headers.Contains("Set-Cookie"));
        using var staleLogout = await SendAsync("logout", first.Id);
        Assert.Equal(HttpStatusCode.Conflict, staleLogout.StatusCode);
        Assert.False(staleLogout.Headers.Contains("Set-Cookie"));
        using var staleChange = await SendAsync("change-password", first.Id, firstSession.AccessToken,
            JsonContent.Create(new { currentPassword = "first-password", newPassword = "changed-password" }));
        Assert.Equal(HttpStatusCode.Conflict, staleChange.StatusCode);
        Assert.False(staleChange.Headers.Contains("Set-Cookie"));
        using var mobile = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/mobile/refresh")
        {
            Content = JsonContent.Create(new RefreshRequest(secondSession.RefreshToken, "test"))
        };
        mobile.Headers.Add("X-Expected-User-Id", first.Id.ToString());
        using var staleMobile = await client.SendAsync(mobile, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, staleMobile.StatusCode);
        Assert.Contains("session_account_mismatch", await staleMobile.Content.ReadAsStringAsync(Ct));

        Assert.Equal(0, await s.Db.RefreshTokens.CountAsync(x => x.UserId == second.Id && x.RevokedAt != null, Ct));
        Assert.Equal(0, await s.Db.AuditLogs.CountAsync(x => x.Action == "user.password-changed", Ct));
        using var valid = await SendAsync("refresh", second.Id);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.True(valid.Headers.Contains("Set-Cookie"));
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task StartingWithARestoredLegacySchemaAdvancesTheSessionVersionOnce()
    {
        await using var s = await fixture.OpenAsync();
        var account = await s.Catalog.CreateUserAsync(new("restored", "password-123", "Administrator", null), Ct);
        var old = await s.Auth.LoginAsync(new("restored", "password-123", "backup"), Ct);
        await s.Db.GetService<IMigrator>().MigrateAsync("20261105160637_EventProcessingResult", Ct);
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE asset_inventory_restore_sessions (previous_version integer NOT NULL);
            INSERT INTO asset_inventory_restore_sessions VALUES (7);
            UPDATE refresh_tokens SET "RevokedAt" = now() WHERE "RevokedAt" IS NULL;
            """, Ct);

        await s.Services.ApplyDatabaseMigrationsAsync(Ct);
        Assert.Equal(8, await s.Db.Users.AsNoTracking().Where(x => x.Id == account.Id).Select(x => x.SecurityVersion).SingleAsync(Ct));
        Assert.False(await s.Db.Database.SqlQueryRaw<bool>(
            """SELECT to_regclass('public.asset_inventory_restore_sessions') IS NOT NULL AS "Value" """).SingleAsync(Ct));
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Auth.RefreshAsync(new(old.RefreshToken, null), Ct));
        await s.Services.ApplyDatabaseMigrationsAsync(Ct);
        Assert.Equal(8, await s.Db.Users.AsNoTracking().Where(x => x.Id == account.Id).Select(x => x.SecurityVersion).SingleAsync(Ct));
        await using var freshScope = s.CreateScope();
        var auth = freshScope.ServiceProvider.GetRequiredService<IAuthService>();
        var fresh = await auth.LoginAsync(new("restored", "password-123", "new"), Ct);
        Assert.Equal("8", new JwtSecurityTokenHandler().ReadJwtToken(fresh.AccessToken).Claims.Single(x => x.Type == "sv").Value);
        Assert.NotNull(await auth.RefreshAsync(new(fresh.RefreshToken, null), Ct));
    }

    [Fact]
    public async Task ConcurrentPasswordChangesAcceptTheOldPasswordOnlyOnce()
    {
        await using var s = await fixture.OpenAsync();
        var user = await s.Catalog.CreateUserAsync(new("owner", "old-password", "Administrator", null), Ct);
        async Task<bool> ChangeAsync(string password)
        {
            await using var scope = s.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<IAuthService>().ChangePasswordAsync(user.Id, new("old-password", password, "test"), Ct); return true; }
            catch (UnauthorizedException) { return false; }
        }
        Assert.Single(await Task.WhenAll(ChangeAsync("new-password-1"), ChangeAsync("new-password-2")), x => x);
        Assert.Equal(1, (await s.Db.Users.AsNoTracking().SingleAsync(Ct)).SecurityVersion);
        Assert.Single(await s.Db.AuditLogs.Where(x => x.Action == "user.password-changed").ToListAsync(Ct));
    }

    [Fact]
    public async Task PasswordChangeAndRefreshCannotDeadlockOrLeaveAnOldSessionUsable()
    {
        await using var s = await fixture.OpenAsync();
        var user = await s.Catalog.CreateUserAsync(new("owner", "old-password", "Administrator", null), Ct);
        var initial = await s.Auth.LoginAsync(new("owner", "old-password", null), Ct);
        await using var rotation = s.CreateScope();
        await using var change = s.CreateScope();
        var changeTask = change.ServiceProvider.GetRequiredService<IAuthService>().ChangePasswordAsync(user.Id, new("old-password", "new-password", "test"), Ct);
        var refreshTask = RotateAsync();
        async Task RotateAsync()
        {
            try { await rotation.ServiceProvider.GetRequiredService<IAuthService>().RefreshAsync(new(initial.RefreshToken, null), Ct); }
            catch (UnauthorizedException) { }
        }
        await Task.WhenAll(changeTask, refreshTask).WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(1, (await s.Db.Users.AsNoTracking().SingleAsync(Ct)).SecurityVersion);
        Assert.Equal(1, await s.Db.RefreshTokens.CountAsync(x => x.RevokedAt == null && x.SecurityVersion == 1, Ct));
    }

    [Theory]
    [InlineData("wrong", "new-password")]
    [InlineData("old-password", "short")]
    [InlineData("old-password", "old-password")]
    public async Task RejectedChangesKeepPasswordAndSessionValid(string current, string password)
    {
        await using var s = await fixture.OpenAsync();
        var user = await s.Catalog.CreateUserAsync(new("owner", "old-password", "Administrator", null), Ct);
        var tokens = await s.Auth.LoginAsync(new("owner", "old-password", null), Ct);
        await Assert.ThrowsAnyAsync<AssetInventory.Application.Common.ApplicationException>(() => s.Auth.ChangePasswordAsync(user.Id, new(current, password, null), Ct));
        Assert.NotNull(await s.Auth.RefreshAsync(new(tokens.RefreshToken, null), Ct));
        Assert.NotNull(await s.Auth.LoginAsync(new("owner", "old-password", null), Ct));
        Assert.Equal(0, await s.Db.AuditLogs.CountAsync(x => x.Action == "user.password-changed", Ct));
    }
}
