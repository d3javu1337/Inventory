using System.Net;
using System.Security.Claims;
using AssetInventory.Application.Assets;
using AssetInventory.Application.Auth;
using AssetInventory.Application.Catalog;
using AssetInventory.Application.Inventories;
using AssetInventory.Application.Common;
using AssetInventory.Infrastructure;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

// Each test class starts its own PostgreSQL container. Bound concurrent fixtures on local Docker.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]

namespace AssetInventory.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public ValueTask InitializeAsync() => new(postgres.StartAsync());
    public ValueTask DisposeAsync() => postgres.DisposeAsync();

    public async Task<TestSession> OpenAsync(TimeProvider? timeProvider = null, string? redisConnection = null)
    {
        // Each test owns a database in the disposable container; no deployment connection string is used.
        var database = "test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
        await command.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = database }.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSql"] = connectionString, ["ConnectionStrings:Redis"] = redisConnection ?? "localhost:6379",
            ["Jwt:SigningKey"] = "integration-tests-only-key-at-least-32-characters-long",
            ["Jwt:Issuer"] = "tests", ["Jwt:Audience"] = "tests", ["FileStorage:RootPath"] = Path.Combine(Path.GetTempPath(), "asset-inventory-tests", database),
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure(configuration);
        if (timeProvider is not null) services.AddSingleton(timeProvider);
        services.AddSingleton<IAuditContext>(new TestAuditContext());
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, TraceIdentifier = "integration-correlation" };
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, TestSession.ActorId.ToString())], "test"));
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var session = new TestSession(provider, scope, http, configuration);
        await session.Db.Database.MigrateAsync();
        return session;
    }
}

internal sealed class TestAuditContext : IAuditContext
{
    public Guid? UserId => TestSession.ActorId;
    public string? IpAddress => "127.0.0.1";
    public string? CorrelationId => "integration-correlation";
}

public sealed class TestSession(ServiceProvider provider, AsyncServiceScope scope, HttpContext http, IConfiguration configuration) : IAsyncDisposable
{
    public static Guid ActorId { get; } = Guid.Parse("01992e83-1234-7000-8000-123456789abc");
    public IServiceProvider Services => scope.ServiceProvider;
    public HttpContext Http => http;
    public IConfiguration Configuration => configuration;
    public AppDbContext Db => Services.GetRequiredService<AppDbContext>();
    public ICatalogService Catalog => Services.GetRequiredService<ICatalogService>();
    public IAssetService Assets => Services.GetRequiredService<IAssetService>();
    public IInventoryService Inventories => Services.GetRequiredService<IInventoryService>();
    public IAuthService Auth => Services.GetRequiredService<IAuthService>();
    public IMonitoringService Monitoring => Services.GetRequiredService<IMonitoringService>();
    public AsyncServiceScope CreateScope() => provider.CreateAsyncScope();
    public async ValueTask DisposeAsync() { await scope.DisposeAsync(); await provider.DisposeAsync(); }
}
