using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AssetInventory.Infrastructure.Persistence;

internal static class RegistryWriteGuard
{
    public static async Task<IDbContextTransaction?> BeginAsync(AppDbContext db, bool shared, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try {
            // Asset writes may run together. Catalog/access changes take the
            // exclusive form of this same gate, keeping reference checks valid
            // until the asset transaction (including a whole import) commits.
            var sql = shared ? "SELECT pg_advisory_xact_lock_shared(736412891)" : "SELECT pg_advisory_xact_lock(736412891)";
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); throw; }
        return transaction;
    }
}
