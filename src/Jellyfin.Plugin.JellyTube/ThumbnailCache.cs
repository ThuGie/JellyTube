using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.JellyTube;

public sealed class ThumbnailCache(IHttpClientFactory clients) : IDisposable
{
    private const int MaxImageSize = 512 * 1024;
    private readonly MemoryCache _images = new(new MemoryCacheOptions { SizeLimit = 24 * 1024 * 1024 });
    private readonly SemaphoreSlim _requests = new(4);

    public async Task<byte[]?> Get(string id, CancellationToken ct)
    {
        Rules.VideoId(id);
        if (_images.TryGetValue(id, out byte[]? cached)) return cached;
        if (!await _requests.WaitAsync(TimeSpan.FromSeconds(2), ct)) return null;
        try
        {
            if (_images.TryGetValue(id, out cached)) return cached;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await clients.CreateClient("JellyTube.Catalog")
                .GetAsync($"https://i.ytimg.com/vi/{id}/hqdefault.jpg", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var length = response.Content.Headers.ContentLength;
            if (!response.IsSuccessStatusCode || length is null or <= 0 or > MaxImageSize
                || response.Content.Headers.ContentType?.MediaType != "image/jpeg") return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (bytes.Length != length) return null;
            _images.Set(id, bytes, new MemoryCacheEntryOptions
                { Size = bytes.Length, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) });
            return bytes;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { _requests.Release(); }
    }

    public void Dispose()
    {
        _images.Dispose();
        _requests.Dispose();
    }
}
