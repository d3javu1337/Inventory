using System.Net;
using Npgsql;

namespace AssetInventory.Api.Configuration;

public static class ProductionSecurity
{
    public static void Validate(IConfiguration configuration, bool development)
    {
        if (development) return;
        var signingKey = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32 || signingKey.StartsWith("development-only", StringComparison.OrdinalIgnoreCase)
            || signingKey.StartsWith("change_me", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production требует отдельный Jwt:SigningKey (минимум 32 символа), переданный через environment/secrets.");
        var database = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("PostgreSql"));
        if (string.IsNullOrWhiteSpace(database.Password) || database.Password is "asset_inventory_dev" or "change_me")
            throw new InvalidOperationException("Production требует отдельный пароль PostgreSQL через environment/secrets.");
        var origins = configuration.GetSection("Cors:Origins").Get<string[]>();
        if (origins is null || origins.Length == 0 || origins.Any(origin => !IsHttpsOrigin(origin)))
            throw new InvalidOperationException("Production требует точный непустой список HTTPS origins в Cors:Origins (без путей и wildcard).");
        foreach (var value in configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
            if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("ReverseProxy:KnownProxies должен содержать конкретные IP доверенных proxy.");
    }

    private static bool IsHttpsOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && !uri.Host.Contains('*') && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
