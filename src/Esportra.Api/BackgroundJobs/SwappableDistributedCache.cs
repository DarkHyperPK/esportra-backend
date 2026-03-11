using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Esportra.Api.BackgroundJobs;

/// <summary>
/// Thread-safe IDistributedCache proxy that starts with an in-memory fallback
/// and can be atomically swapped to a Redis-backed implementation at runtime
/// once RedisBackgroundConnector calls .Swap() when Redis connects.
/// </summary>
public sealed class SwappableDistributedCache : IDistributedCache
{
    private volatile IDistributedCache _inner =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    public void Swap(IDistributedCache newCache) =>
        Interlocked.Exchange(ref _inner, newCache);

    public byte[]? Get(string key) => _inner.Get(key);
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);
    public void Refresh(string key) => _inner.Refresh(key);
    public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
    public void Remove(string key) => _inner.Remove(key);
    public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(key, value, options, token);
}
