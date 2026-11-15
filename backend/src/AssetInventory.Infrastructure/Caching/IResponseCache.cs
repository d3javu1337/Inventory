namespace AssetInventory.Infrastructure.Caching;

public interface IResponseCache
{
    Task<T> GetAsync<T>(string group, string variant, Func<Task<T>> load, CancellationToken ct);
    Task InvalidateAsync(IEnumerable<string> groups);
}
