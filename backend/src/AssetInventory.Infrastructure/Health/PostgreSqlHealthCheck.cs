using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AssetInventory.Infrastructure.Health;

internal sealed class PostgreSqlHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);

            return canConnect
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL connection failed.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL health check failed.", exception);
        }
    }
}
