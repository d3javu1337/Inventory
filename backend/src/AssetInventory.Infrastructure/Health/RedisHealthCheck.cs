using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AssetInventory.Infrastructure.Health;

internal sealed class RedisHealthCheck(IDistributedCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.GetAsync("health:ready", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Redis health check failed.", exception);
        }
    }
}
