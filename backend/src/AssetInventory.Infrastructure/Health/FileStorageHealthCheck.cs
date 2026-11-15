using AssetInventory.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AssetInventory.Infrastructure.Health;

internal sealed class FileStorageHealthCheck(IOptions<FileStorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string? probe = null;
        try
        {
            var root = Path.Combine(Path.GetFullPath(options.Value.RootPath), "reports");
            Directory.CreateDirectory(root);
            probe = Path.Combine(root, ".health-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(probe, "ready", cancellationToken);
            return await File.ReadAllTextAsync(probe, cancellationToken) == "ready"
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("File storage read failed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return HealthCheckResult.Unhealthy("File storage is not writable."); }
        finally { if (probe is not null && File.Exists(probe)) File.Delete(probe); }
    }
}
