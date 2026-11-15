using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using AssetInventory.Domain;

namespace AssetInventory.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task ApplyDatabaseMigrationsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
        await ApplyRestoreSessionBoundaryAsync(dbContext, cancellationToken);
    }

    private static async Task ApplyRestoreSessionBoundaryAsync(AppDbContext db, CancellationToken ct)
    {
        // A pre-SecurityVersion backup cannot rotate JWT versions until its schema is upgraded.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(635742701)", ct);
        var pending = await db.Database.SqlQueryRaw<bool>(
            """SELECT to_regclass('public.asset_inventory_restore_sessions') IS NOT NULL AS "Value" """).SingleAsync(ct);
        if (pending)
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE user_accounts SET security_version = 1 + GREATEST(
                    (SELECT COALESCE(MAX(security_version), 0) FROM user_accounts),
                    (SELECT COALESCE(MAX(previous_version), 0) FROM asset_inventory_restore_sessions));
                DROP TABLE asset_inventory_restore_sessions;
                """, ct);
        }
        await transaction.CommitAsync(ct);
    }

    public static async Task SeedAdministratorAsync(this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await dbContext.Users.AnyAsync(cancellationToken)) return;

        var login = configuration["Seed:AdminLogin"] ?? "admin";
        var user = new UserAccount { Login = login, NormalizedLogin = login.ToUpperInvariant(), PasswordHash = string.Empty, Role = UserRole.Administrator };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
