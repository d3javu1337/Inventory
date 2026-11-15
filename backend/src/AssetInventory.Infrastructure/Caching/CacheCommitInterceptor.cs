using System.Data.Common;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AssetInventory.Infrastructure.Caching;

internal sealed class CacheCommitInterceptor : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    { if (eventData.Context is AppDbContext db) db.PublishCacheInvalidationsAsync().GetAwaiter().GetResult(); }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        => eventData.Context is AppDbContext db ? db.PublishCacheInvalidationsAsync() : Task.CompletedTask;

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    { if (eventData.Context is AppDbContext db) db.ClearCacheInvalidations(); }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { if (eventData.Context is AppDbContext db) db.ClearCacheInvalidations(); return Task.CompletedTask; }
}
