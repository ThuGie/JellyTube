using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Small, bounded cache of public media ranges. No account data, signed URLs or credentials in keys.</summary>
public sealed class SegmentCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });
    private readonly SemaphoreSlim _streams = new(8);
    public async Task<IDisposable> Enter(CancellationToken ct)
    {
        if (!await _streams.WaitAsync(TimeSpan.FromSeconds(15), ct)) throw new ProviderException("Streaming capacity is busy. Retry playback shortly.");
        return new Lease(_streams);
    }
    private sealed class Lease(SemaphoreSlim slots) : IDisposable { public void Dispose() => slots.Release(); }
    public byte[]? Get(string key) => _cache.TryGetValue(key, out byte[]? bytes) ? bytes : null;
    public void Put(string key, byte[] bytes)
    {
        if (bytes.Length <= 4 * 1024 * 1024) _cache.Set(key, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = TimeSpan.FromMinutes(2), AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
    }
    public void Dispose() { _cache.Dispose(); _streams.Dispose(); }
}
