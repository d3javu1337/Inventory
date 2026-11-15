using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

namespace AssetInventory.Infrastructure.Caching;

internal sealed class RedisResponseCache : IResponseCache, IDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;
    private readonly string prefix;
    private readonly ILogger<RedisResponseCache> logger;
    private readonly ConcurrentDictionary<string, Guid> pendingInvalidations = new();
    private long unavailableUntil;

    public RedisResponseCache(IConfiguration configuration, ILogger<RedisResponseCache> logger)
    {
        this.logger = logger;
        var database = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("PostgreSql"));
        // Different databases sharing Redis must never share responses. Credentials are not part of keys.
        prefix = "asset-inventory:response:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{database.Host}:{database.Port}:{database.Database}:{database.Username}"))) + ":";
        connection = new(() =>
        {
            var options = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis")!);
            options.AbortOnConnectFail = false; options.ConnectTimeout = 500; options.AsyncTimeout = 500;
            return ConnectionMultiplexer.ConnectAsync(options);
        });
    }

    public async Task<T> GetAsync<T>(string group, string variant, Func<Task<T>> load, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pendingInvalidations.TryGetValue(group, out var pending))
        {
            await PublishInvalidationAsync(group, pending, ct);
            if (pendingInvalidations.ContainsKey(group)) return await load();
        }
        var version = await TryAsync(async db =>
        {
            var key = prefix + group + ":generation";
            var current = await db.StringGetAsync(key);
            if (current.HasValue) return current.ToString();
            var fresh = Guid.NewGuid().ToString("N");
            if (await db.StringSetAsync(key, fresh, when: When.NotExists)) return fresh;
            return (await db.StringGetAsync(key)).ToString();
        }, ct);
        if (string.IsNullOrEmpty(version)) return await load();
        var entryKey = prefix + group + ":" + version + ":" + variant;
        var payload = await TryAsync(async db => (await db.StringGetAsync(entryKey)).ToString(), ct);
        if (!string.IsNullOrEmpty(payload))
        {
            try { return JsonSerializer.Deserialize<T>(payload)!; }
            catch (JsonException) { logger.LogWarning("Поврежденный response cache, группа {Group}; чтение PostgreSQL", group); }
        }
        var result = await load();
        // A late reader can publish only into its original generation; new readers use the new key after commit.
        await TryAsync(async db => await db.StringSetAsync(entryKey, JsonSerializer.Serialize(result), TimeSpan.FromSeconds(group == "dashboard" ? 5 : 30)), ct);
        return result;
    }

    public async Task InvalidateAsync(IEnumerable<string> groups)
    {
        foreach (var group in groups.Distinct())
        {
            var pending = Guid.NewGuid();
            pendingInvalidations[group] = pending;
            await PublishInvalidationAsync(group, pending, CancellationToken.None);
        }
    }

    private async Task PublishInvalidationAsync(string group, Guid pending, CancellationToken ct)
    {
        // A timed-out Redis write may still execute later. Every attempt uses a
        // fresh generation, and a successful older attempt cannot clear a newer edit.
        var generation = Guid.NewGuid().ToString("N");
        if (await TryAsync(db => db.StringSetAsync(prefix + group + ":generation", generation), ct))
            pendingInvalidations.TryRemove(new KeyValuePair<string, Guid>(group, pending));
    }

    private async Task<T?> TryAsync<T>(Func<IDatabase, Task<T>> operation, CancellationToken ct)
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref unavailableUntil)) return default;
        try
        {
            var redis = await connection.Value.WaitAsync(TimeSpan.FromMilliseconds(200), ct);
            return await operation(redis.GetDatabase()).WaitAsync(TimeSpan.FromMilliseconds(200), ct);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            Interlocked.Exchange(ref unavailableUntil, DateTime.UtcNow.AddSeconds(5).Ticks);
            logger.LogWarning("Response cache недоступен ({ExceptionType}); PostgreSQL используется напрямую", exception.GetType().Name);
            return default;
        }
    }

    public void Dispose()
    {
        if (connection.IsValueCreated)
            _ = connection.Value.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
    }
}
