using AssetInventory.Infrastructure.Health;
using AssetInventory.Infrastructure.Persistence;
using AssetInventory.Infrastructure.Storage;
using AssetInventory.Application.Auth;
using AssetInventory.Application.Catalog;
using AssetInventory.Infrastructure.Catalog;
using AssetInventory.Application.Assets;
using AssetInventory.Infrastructure.Assets;
using AssetInventory.Application.Inventories;
using AssetInventory.Infrastructure.Inventories;
using AssetInventory.Infrastructure.Documents;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Security;
using AssetInventory.Application.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AssetInventory.Infrastructure.Caching;

namespace AssetInventory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var postgreSql = RequireConnectionString(configuration, "PostgreSql");
        var redis = RequireConnectionString(configuration, "Redis");

        services.AddSingleton<IResponseCache>(provider => new RedisResponseCache(configuration, provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RedisResponseCache>>()));
        services.AddSingleton<CacheCommitInterceptor>();
        services.AddDbContext<AppDbContext>((provider, options) => options.UseNpgsql(postgreSql).AddInterceptors(provider.GetRequiredService<CacheCommitInterceptor>()));
        services.AddHttpContextAccessor();
        services.AddScoped<IAuditContext, HttpAuditContext>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddSingleton<IAuthAttemptLimiter, RedisAuthAttemptLimiter>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IAssetService, AssetService>();
        services.AddScoped<IAssetImportService, AssetImportService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IIntegrationEventService, IntegrationEventService>();
        services.AddScoped<IInventoryJobProcessor, InventoryJobProcessor>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IMonitoringService, MonitoringService>();
        if (configuration.GetValue("InventoryWorker:Enabled", true)) services.AddHostedService<InventoryWorker>();
        services.AddScoped<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();

        var jwtSection = configuration.GetSection(JwtOptions.SectionName);
        var jwt = jwtSection.Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt configuration is required.");
        services.AddOptions<JwtOptions>()
            .Bind(jwtSection)
            .Validate(x => x.SigningKey.Length >= 32, "Jwt:SigningKey must contain at least 32 characters.")
            .ValidateOnStart();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                        if (!Guid.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                            || !int.TryParse(context.Principal?.FindFirstValue("sv") ?? "0", out var securityVersion)
                            || !await UserAccess.IsActiveAsync(db, userId, context.HttpContext.RequestAborted, securityVersion))
                            context.Fail("Сессия отозвана или учетная запись недоступна.");
                    }
                };
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });
        services.AddAuthorization();
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redis;
            options.InstanceName = "asset-inventory:";
        });

        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "FileStorage:RootPath is required.")
            .ValidateOnStart();

        services.AddHealthChecks()
            .AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"])
            .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
            .AddCheck<FileStorageHealthCheck>("storage", tags: ["ready"]);

        return services;
    }

    private static string RequireConnectionString(IConfiguration configuration, string name)
    {
        return configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' is required.");
    }
}
