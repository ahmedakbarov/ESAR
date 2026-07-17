using System.Collections.Concurrent;
using System.Text.Json;
using Esar.Application.Abstractions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Esar.Infrastructure.Caching;

public class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value!);
        }
        catch (Exception ex)
        {
            // Cache must never take the platform down — degrade to a miss.
            _logger.LogWarning(ex, "Redis GET failed for {Key}", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        try
        {
            await _redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value),
                ttl ?? TimeSpan.FromMinutes(10));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis SET failed for {Key}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        try { await _redis.GetDatabase().KeyDeleteAsync(key); }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis DEL failed for {Key}", key); }
    }

    public async Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        try
        {
            foreach (var endpoint in _redis.GetEndPoints())
            {
                var server = _redis.GetServer(endpoint);
                await foreach (var key in server.KeysAsync(pattern: $"{prefix}*").WithCancellation(ct))
                    await _redis.GetDatabase().KeyDeleteAsync(key);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis prefix delete failed for {Prefix}", prefix); }
    }
}

/// <summary>In-process fallback used in development/tests when Redis is not configured.</summary>
public class MemoryCacheService : ICacheService
{
    private readonly ConcurrentDictionary<string, (string Json, DateTime Expires)> _store = new();

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_store.TryGetValue(key, out var entry) && entry.Expires > DateTime.UtcNow)
            return Task.FromResult(JsonSerializer.Deserialize<T>(entry.Json));
        _store.TryRemove(key, out _);
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        _store[key] = (JsonSerializer.Serialize(value), DateTime.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(10)));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix)))
            _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
