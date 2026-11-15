using AssetInventory.Application.Auth;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using AssetInventory.Api.Endpoints;
using AssetInventory.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using AssetInventory.Infrastructure;

namespace AssetInventory.IntegrationTests;

public sealed class RedisLimiterTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task RedisCountersAreAtomicAcrossInstancesAndResetAllowsLoginAgain()
    {
        await using var redis = new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true).Build();
        await redis.StartAsync(Ct);
        var endpoint = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)},abortConnect=false";
        await using var first = await fixture.OpenAsync(redisConnection: endpoint);
        await using var second = await fixture.OpenAsync(redisConnection: endpoint);
        var a = first.Services.GetRequiredService<IAuthAttemptLimiter>();
        var b = second.Services.GetRequiredService<IAuthAttemptLimiter>();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? a : b).CheckAsync(i % 2 == 0 ? " Admin " : "admin", Ct)));
        Assert.Equal(10, results.Count(x => x.Allowed));
        Assert.All(results.Where(x => !x.Allowed), x => Assert.InRange(x.RetryAfterSeconds, 1, 60));
        Assert.True((await a.CheckAsync("other-account", Ct)).Allowed);
        using var connection = await ConnectionMultiplexer.ConnectAsync(endpoint);
        var server = connection.GetServer(endpoint.Split(',')[0]);
        foreach (var key in server.Keys(pattern: "asset-inventory:auth:*"))
        {
            Assert.NotNull(await connection.GetDatabase().KeyTimeToLiveAsync(key));
            await connection.GetDatabase().KeyExpireAsync(key, TimeSpan.FromMilliseconds(1));
        }
        await Task.Delay(30, Ct);
        Assert.True((await b.CheckAsync("admin", Ct)).Allowed);
    }

    [Fact]
    public async Task HttpLoginLimitIsSharedBetweenWebAndMobileAndDoesNotBlockLogout()
    {
        await using var redis = new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true).Build();
        await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}");
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", null), Ct);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Configuration.AddConfiguration(s.Configuration);
        builder.Services.AddInfrastructure(builder.Configuration); builder.Services.RemoveAll<IHostedService>();
        await using var app = builder.Build(); app.Urls.Add("http://127.0.0.1:0");
        app.UseMiddleware<ApplicationExceptionMiddleware>(); app.MapAuthEndpoints();
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        for (var i = 0; i < 10; i++)
        {
            using var response = await client.PostAsJsonAsync($"/api/v1/auth/{(i % 2 == 0 ? "web" : "mobile")}/login/start", new { login = "admin" }, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var blocked = await client.PostAsJsonAsync("/api/v1/auth/mobile/login", new { login = "admin", password = "password-123" }, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
        using var logout = await client.PostAsJsonAsync("/api/v1/auth/mobile/logout", new { refreshToken = "not-a-token" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task RedisOutageUsesBoundedInProcessLimiter()
    {
        await using var redis = new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true).Build();
        await redis.StartAsync(Ct);
        await using var s = await fixture.OpenAsync(redisConnection: $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}");
        var limiter = s.Services.GetRequiredService<IAuthAttemptLimiter>();
        Assert.True((await limiter.CheckAsync("warmup", Ct)).Allowed);
        await redis.StopAsync(Ct);
        var decisions = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => limiter.CheckAsync("outage", Ct)));
        Assert.Equal(10, decisions.Count(x => x.Allowed));
        Assert.True((await limiter.CheckAsync("another", Ct)).Allowed);
    }
}
