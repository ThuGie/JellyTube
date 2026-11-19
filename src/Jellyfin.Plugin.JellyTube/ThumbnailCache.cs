using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.JellyTube;

public sealed class ThumbnailCache(IHttpClientFactory clients) : IDisposable
{
    private const int MaxImageSize = 512 * 1024;
    private readonly MemoryCache _images = new(new MemoryCacheOptions { SizeLimit = 24 * 1024 * 1024 });
    private readonly SemaphoreSlim _requests = new(4);
    private readonly KeyedGate _fills = new();
    private readonly SemaphoreSlim _prune = new(1);
    private long _diskBytes = -1;
    private readonly string? _cacheDirectory;
    private readonly long _diskBudget = 128L * 1024 * 1024;
    internal ThumbnailCache(IHttpClientFactory clients, string directory, long budget) : this(clients)
    { _cacheDirectory = directory; _diskBudget = budget; }
    private string DirectoryPath => _cacheDirectory ?? Path.Combine(Plugin.Instance.DataFolderPath, "thumbnails");

    public async Task<byte[]?> Get(string id, CancellationToken ct)
    {
        Rules.VideoId(id);
        if (_images.TryGetValue(id, out byte[]? cached)) return cached;
        using var fill = await _fills.Enter(id, ct);
        if (_images.TryGetValue(id, out cached)) return cached;
        var path = Path.Combine(DirectoryPath, id + ".jpg");
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length is > 0 and <= MaxImageSize && info.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-6))
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                _images.Set(id, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) });
                Performance.Record("thumbnail.disk.hit", 0);
                return bytes;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
            try
            {
                await SaveDisk(path, bytes, timeout.Token);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return bytes;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { _requests.Release(); }
    }

    private async Task SaveDisk(string path, byte[] bytes, CancellationToken ct)
    {
        await _prune.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (_diskBytes < 0) _diskBytes = new DirectoryInfo(DirectoryPath).EnumerateFiles("*.jpg").Sum(file => file.Length);
            var previous = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (_diskBytes - previous + bytes.Length > _diskBudget)
            {
                foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.jpg").OrderBy(file => file.LastWriteTimeUtc))
                {
                    if (file.FullName == path) continue;
                    var length = file.Length; file.Delete(); _diskBytes -= length;
                    if (_diskBytes - previous + bytes.Length <= _diskBudget) break;
                }
            }
            if (bytes.Length > _diskBudget) return;
            var temporary = path + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes, ct); File.Move(temporary, path, true); }
            finally { File.Delete(temporary); }
            _diskBytes += bytes.Length - previous;
        }
        finally { _prune.Release(); }
    }

    public void Dispose()
    {
        _images.Dispose();
        _requests.Dispose();
        _prune.Dispose();
    }
}
