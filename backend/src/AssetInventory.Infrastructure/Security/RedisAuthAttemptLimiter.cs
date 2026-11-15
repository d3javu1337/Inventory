using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AssetInventory.Application.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AssetInventory.Infrastructure.Security;

internal sealed class RedisAuthAttemptLimiter(IConfiguration configuration, TimeProvider time, ILogger<RedisAuthAttemptLimiter> logger) : IAuthAttemptLimiter, IDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection = new(() =>
    {
        var options = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis")!);
        options.AbortOnConnectFail = false; options.ConnectTimeout = 1000; options.AsyncTimeout = 1000;
        return ConnectionMultiplexer.ConnectAsync(options);
    });
    private readonly ConcurrentDictionary<string, Window> fallback = new();
    private sealed class Window { public DateTimeOffset Started; public int Count; }
    public async Task<AuthAttemptDecision> CheckAsync(string login, CancellationToken ct)
    {
        var key = "asset-inventory:auth:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(login.Trim().ToUpperInvariant())));
        try
        {
            var redis = await connection.Value.WaitAsync(TimeSpan.FromSeconds(2), ct);
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync("local n = redis.call('INCR', KEYS[1]); if n == 1 then redis.call('EXPIRE', KEYS[1], 60) end; return { n, redis.call('TTL', KEYS[1]) }", [new RedisKey(key)]).WaitAsync(ct))!;
            return new((long)result[0] <= 10, Math.Max(1, (int)(long)result[1]));
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            logger.LogWarning("Redis недоступен: ограничение входа временно работает в памяти процесса ({ExceptionType})", exception.GetType().Name);
            var now = time.GetUtcNow();
            if (fallback.Count >= 10_000)
                foreach (var entry in fallback.Where(x => now - x.Value.Started >= TimeSpan.FromMinutes(1))) fallback.TryRemove(entry.Key, out _);
            if (fallback.Count >= 10_000 && !fallback.ContainsKey(key)) return new(false, 60);
            var window = fallback.GetOrAdd(key, _ => new Window { Started = now });
            lock (window)
            {
                if (now - window.Started >= TimeSpan.FromMinutes(1)) { window.Started = now; window.Count = 0; }
                return new(++window.Count <= 10, Math.Max(1, (int)Math.Ceiling(60 - (now - window.Started).TotalSeconds)));
            }
        }
    }
    public void Dispose() { if (connection.IsValueCreated && connection.Value.IsCompletedSuccessfully) connection.Value.Result.Dispose(); }
}
