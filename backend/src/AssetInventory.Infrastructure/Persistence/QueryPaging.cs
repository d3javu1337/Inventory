using AssetInventory.Application.Common;
using AssetInventory.Contracts.Common;
using Microsoft.EntityFrameworkCore;
namespace AssetInventory.Infrastructure.Persistence;
internal static class QueryPaging
{
    public static void Validate(PageQuery query)
    {
        if (query.Page < 1 || query.Page > 1_000_000 || query.PageSize < 1 || query.PageSize > 200) throw new RequestValidationException("Страница должна быть положительной, размер — от 1 до 200.");
        if (query.Search?.Length > 200) throw new RequestValidationException("Поиск не может быть длиннее 200 символов.");
    }
    public static async Task<PagedResponse<T>> PageAsync<T>(this IQueryable<T> source, PageQuery query, CancellationToken ct)
    {
        Validate(query);
        var total = await source.CountAsync(ct);
        var page = Math.Min(query.Page, Math.Max(1, (int)Math.Ceiling(total / (double)query.PageSize)));
        return new(await source.Skip((page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct), page, query.PageSize, total);
    }
}
