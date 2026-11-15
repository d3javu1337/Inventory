using AssetInventory.Application.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetInventory.Infrastructure.Persistence;

internal static class CommandPersistence
{
    public static async Task SaveCommandAsync(this AppDbContext db, CancellationToken ct, Func<AssetInventory.Application.Common.ApplicationException>? concurrencyError = null)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw concurrencyError?.Invoke() ?? new ConflictException("Данные изменены другим пользователем. Загрузите актуальную карточку и повторите действие.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ConflictException("Запись с такими уникальными реквизитами уже существует. Обновите список."); }
    }
}
