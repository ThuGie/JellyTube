using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Small, bounded cache of public media ranges. No account data, signed URLs or credentials in keys.</summary>
public sealed class SegmentCache : IDisposable
{
    internal static string Key(string id, string track, string fingerprint, long? start, long? end) =>
        FormattableString.Invariant($"{id}|{track}|{fingerprint}|{start}|{end}");
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });
    private readonly SemaphoreSlim _streams = new(8);
    private readonly SemaphoreSlim[] _fills = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public async Task<IDisposable> Enter(CancellationToken ct)
    {
        if (!await _streams.WaitAsync(TimeSpan.FromSeconds(15), ct)) throw new ProviderException("Streaming capacity is busy. Retry playback shortly.");
        return new Lease(_streams);
    }
    private sealed class Lease(SemaphoreSlim slots) : IDisposable { public void Dispose() => slots.Release(); }
    public async Task<IDisposable> EnterFill(string key, CancellationToken ct)
    {
        var slot = _fills[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_fills.Length)];
        await slot.WaitAsync(ct);
        return new Lease(slot);
    }
    public byte[]? Get(string key) => _cache.TryGetValue(key, out byte[]? bytes) ? bytes : null;
    public void Put(string key, byte[] bytes)
    {
        if (bytes.Length <= 4 * 1024 * 1024) _cache.Set(key, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = TimeSpan.FromMinutes(2), AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
    }
    public void Dispose()
    {
        _cache.Dispose();
        _streams.Dispose();
        foreach (var fill in _fills) fill.Dispose();
    }
}
