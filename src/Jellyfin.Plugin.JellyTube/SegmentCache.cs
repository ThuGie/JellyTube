using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Small, bounded cache of public media ranges. No account data, signed URLs or credentials in keys.</summary>
public sealed class SegmentCache : IDisposable
{
    internal static string Key(string id, string track, string fingerprint, long? start, long? end) =>
        FormattableString.Invariant($"{id}|{track}|{fingerprint}|{start}|{end}");
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });
    private readonly SemaphoreSlim _streams = new(6);
    private readonly SemaphoreSlim _longStreams = new(2);
    private readonly KeyedGate _fills = new();
    public async Task<IDisposable> Enter(CancellationToken ct, bool interactive = true)
    {
        using var timing = Performance.Measure("media.queue");
        var slots = interactive ? _streams : _longStreams;
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(3), ct)) throw new ProviderException("Streaming capacity is busy. Retry playback shortly.");
        return new Lease(slots);
    }
    private sealed class Lease(SemaphoreSlim slots) : IDisposable { public void Dispose() => slots.Release(); }
    public async Task<IDisposable?> EnterFill(string key, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { return await _fills.Enter(key, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
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
        _longStreams.Dispose();

    }
}
