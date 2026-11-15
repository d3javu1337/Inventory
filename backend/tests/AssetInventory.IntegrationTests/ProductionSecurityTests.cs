using AssetInventory.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class ProductionSecurityTests
{
    private static Dictionary<string, string?> Settings() => new()
    {
        ["Jwt:SigningKey"] = "test-production-configuration-key-64-characters-not-for-deployment",
        ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=test;Username=test;Password=unique-test-password",
        ["Cors:Origins:0"] = "https://inventory.example.org",
        ["ReverseProxy:KnownProxies:0"] = "172.30.0.10",
    };
    private static IConfiguration Config(Dictionary<string, string?> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    [Fact]
    public void ExplicitProductionSettingsAreAcceptedAndDevelopmentRemainsLocal()
    {
        ProductionSecurity.Validate(Config(Settings()), false);
        ProductionSecurity.Validate(Config([]), true);
    }

    [Theory]
    [InlineData("Jwt:SigningKey", "development-only-signing-key-change-in-production-2026")]
    [InlineData("Jwt:SigningKey", "short")]
    [InlineData("ConnectionStrings:PostgreSql", "Host=localhost;Password=asset_inventory_dev")]
    [InlineData("ConnectionStrings:PostgreSql", "Host=localhost;Password=change_me")]
    [InlineData("Cors:Origins:0", "http://localhost:5173")]
    [InlineData("Cors:Origins:0", "https://inventory.example.org/api")]
    [InlineData("Cors:Origins:0", "https://*.example.org")]
    [InlineData("ReverseProxy:KnownProxies:0", "0.0.0.0")]
    [InlineData("ReverseProxy:KnownProxies:0", "::")]
    public void UnsafeProductionSettingsAreRejected(string key, string value)
    {
        var settings = Settings(); settings[key] = value;
        Assert.Throws<InvalidOperationException>(() => ProductionSecurity.Validate(Config(settings), false));
    }
}
