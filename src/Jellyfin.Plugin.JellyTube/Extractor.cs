using System.Collections.Concurrent;
using System.Text.Json;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Jellyfin.Plugin.JellyTube;

public sealed class ProviderException(string message) : Exception(message);

/// <summary>Pure managed extraction; no downloader or JavaScript runtime process.</summary>
public sealed class Extractor
{
    private readonly YoutubeClient _youtube;
    private readonly SemaphoreSlim _slots = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<ExtractedSource>>> _pending = new();
    private readonly ConcurrentDictionary<string, ExtractedSource> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<AdaptiveSource>>> _adaptivePending = new();
    private readonly ConcurrentDictionary<string, AdaptiveSource> _adaptiveCache = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, CatalogPage Page)> _uploads = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, VideoInfo Video)> _details = new();
    private readonly IHttpClientFactory _http;
    private readonly SegmentCache _segments;
    public Extractor(IHttpClientFactory http, SegmentCache segments) { _http = http; _segments = segments; _youtube = new(http.CreateClient("JellyTube.YouTube")); }
    public async Task<AdaptiveSource> ResolveAdaptive(string id, int maxHeight, CancellationToken ct)
    {
        Rules.VideoId(id); var key = id + ":" + maxHeight;
        if (_adaptiveCache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached;
        var pending = _adaptivePending.GetOrAdd(key, _ => new(() => AdaptiveCore(id, maxHeight)));
        try
        {
            var source = await pending.Value.WaitAsync(ct);
            if (_adaptiveCache.Count >= 200) foreach (var old in _adaptiveCache.OrderBy(x => x.Value.ExpiresAt).Take(50)) _adaptiveCache.TryRemove(old.Key, out _);
            _adaptiveCache[key] = source; return source;
        }
        finally { if (pending.IsValueCreated && pending.Value.IsCompleted) _adaptivePending.TryRemove(new KeyValuePair<string, Lazy<Task<AdaptiveSource>>>(key, pending)); }
    }
    public void InvalidateAdaptive(string id, int height) => _adaptiveCache.TryRemove(id + ":" + height, out _);
    internal async Task WarmAdaptive(string id, int height)
    {
        try { await ResolveAdaptive(id, height, CancellationToken.None); }
        catch (Exception ex) when (ex is ProviderException or OperationCanceledException)
        {
            // Preparation is best effort. The manifest request can retry and report playback errors.
        }
    }
    private async Task<AdaptiveSource> AdaptiveCore(string id, int height)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _slots.WaitAsync(timeout.Token);
        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(id, timeout.Token);
            var video = manifest.GetVideoOnlyStreams().Where(s => s.Container.Name == "mp4" && s.VideoResolution.Height <= height)
                .OrderByDescending(s => s.VideoCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.VideoResolution.Height).ThenByDescending(s => s.Bitrate).FirstOrDefault()
                ?? throw new ProviderException("No supported adaptive video format is available.");
            var audio = manifest.GetAudioOnlyStreams().Where(s => s.Container.Name == "mp4").OrderByDescending(s => s.Bitrate).FirstOrDefault()
                ?? throw new ProviderException("No supported adaptive audio format is available.");
            var videoIndex = IndexTrack(id, "video", video, video.VideoCodec, video.VideoResolution.Width, video.VideoResolution.Height, timeout.Token);
            var audioIndex = IndexTrack(id, "audio", audio, audio.AudioCodec, 0, 0, timeout.Token);
            await Task.WhenAll(videoIndex, audioIndex);
            return new(await videoIndex, await audioIndex, DateTimeOffset.UtcNow.AddMinutes(5));
        }
        catch (Exception ex) when (ex is not (ProviderException or OperationCanceledException))
        { throw new ProviderException("Managed YouTube extraction failed. The video may be restricted or the extractor needs an update."); }
        finally { _slots.Release(); }
    }
    private async Task<AdaptiveTrack> IndexTrack(string id, string kind, IStreamInfo stream, string codec, int width, int height, CancellationToken ct)
    {
        if (!AllowedMediaUri(stream.Url)) throw new ProviderException("The provider returned an unsupported media destination.");
        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        request.Headers.Range = new(0, 524287);
        using var response = await _http.CreateClient("JellyTube.Media").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("YouTube refused the stream index. Retry later or update JellyTube.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[16384];
        while (output.Length < 524288)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 524288 - output.Length)), ct);
            if (count == 0) break; output.Write(buffer, 0, count);
            try { _ = Mp4Index.Read(output.GetBuffer().AsSpan(0, (int)output.Length)); break; }
            catch (ProviderException) { /* The next block may complete the index; the response is capped above. */ }
        }
        var indexBytes = output.ToArray(); var index = Mp4Index.Segments(indexBytes);
        if (index.Segments.Any(s => s.End >= stream.Size.Bytes)) throw new ProviderException("The stream index exceeds its media bounds.");
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(indexBytes.AsSpan((int)index.Start, (int)(index.End - index.Start + 1))));
        var track = new AdaptiveTrack(stream.Url, codec, stream.Size.Bytes, (long)stream.Bitrate.BitsPerSecond, width, height, index.Start, index.End, index.InitEnd, index.Timescale, index.PresentationTime, index.Segments, identity);
        var fingerprint = MediaController.Fingerprint(track);
        foreach (var (start, end) in new[] { (0L, index.InitEnd), (index.Start, index.End) })
        {
            if (start >= 0 && end >= start && end < indexBytes.Length)
                _segments.Put(SegmentCache.Key(id, kind, fingerprint, start, end), indexBytes[(int)start..((int)end + 1)]);
        }
        return track;
    }
    public async Task<ExtractedSource> Resolve(string id, int maxHeight, CancellationToken ct)
    {
        Rules.VideoId(id); var key = id + ":" + maxHeight;
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached;
        var lazy = _pending.GetOrAdd(key, _ => new(() => ResolveCore(id, maxHeight)));
        try
        {
            var source = await lazy.Value.WaitAsync(ct);
            if (_cache.Count > 250) foreach (var old in _cache.OrderBy(x => x.Value.ExpiresAt).Take(50)) _cache.TryRemove(old.Key, out _);
            _cache[key] = source; return source;
        }
        finally { if (lazy.IsValueCreated && lazy.Value.IsCompleted) _pending.TryRemove(new KeyValuePair<string, Lazy<Task<ExtractedSource>>>(key, lazy)); }
    }
    public void Invalidate(string id, int height) => _cache.TryRemove(id + ":" + height, out _);
    private async Task<ExtractedSource> ResolveCore(string id, int height)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _slots.WaitAsync(timeout.Token);
        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(id, timeout.Token);
            var stream = manifest.GetMuxedStreams().Where(s => s.VideoResolution.Height <= height)
                .OrderByDescending(s => s.Container.Name == "mp4").ThenByDescending(s => s.VideoQuality.MaxHeight).FirstOrDefault();
            if (stream is null) throw new ProviderException("No combined stream is available. Download prepares separate audio/video tracks as a native local copy.");
            if (!AllowedMediaUri(stream.Url)) throw new ProviderException("The provider returned an unsupported media destination.");
            return new(stream.Url, new(), stream.Container.Name, stream.VideoCodec, stream.AudioCodec,
                stream.VideoResolution.Width, stream.VideoResolution.Height, 0, DateTimeOffset.UtcNow.AddMinutes(5), stream.Size.Bytes);
        }
        catch (Exception ex) when (ex is not (ProviderException or OperationCanceledException))
        { throw new ProviderException("YouTube stream extraction failed. The video may be restricted or the managed extractor may need an update."); }
        finally { _slots.Release(); }
    }
    internal static bool AllowedMediaUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);
    public async Task<VideoInfo> Details(string id, CancellationToken ct)
    {
        Rules.VideoId(id);
        if (_details.TryGetValue(id, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Video;
        try
        {
            var video = await _youtube.Videos.GetAsync(id, ct);
            var details = new VideoInfo(id, video.Title, video.Author.ChannelTitle, video.Author.ChannelId,
                "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg", video.Duration?.TotalSeconds, video.Description,
                video.UploadDate, video.Keywords.Take(30).ToArray());
            if (_details.Count >= 256) foreach (var old in _details.OrderBy(entry => entry.Value.Expires).Take(64)) _details.TryRemove(old.Key, out _);
            _details[id] = (DateTimeOffset.UtcNow.AddMinutes(30), details);
            return details;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ProviderException("Video details are unavailable. The video may be private or restricted."); }
    }
    public async Task<CatalogPage> Search(string query, CancellationToken ct, string? page = null)
    {
        var items = new List<VideoInfo>();
        var offset = PageOffset(page);
        var remaining = offset;
        try
        {
            await foreach (var video in _youtube.Search.GetVideosAsync(query, ct))
            {
                if (remaining-- > 0) continue;
                var id = video.Id.Value;
                items.Add(new(id, video.Title, video.Author.ChannelTitle, video.Author.ChannelId,
                    "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg", video.Duration?.TotalSeconds));
                if (items.Count == 21) break;
            }
            return BoundedPage(items, offset);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ProviderException("YouTube search is unavailable. Retry later or use a video URL."); }
    }
    public async Task<ChannelInfo> Channel(string id, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^UC[A-Za-z0-9_-]{22}$")) throw new System.ComponentModel.DataAnnotations.ValidationException("Invalid channel ID.");
        var channel = await _youtube.Channels.GetAsync(id, ct);
        return new(id, channel.Title, channel.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? "");
    }
    public async Task<List<DiscoveryResult>> Discover(string query, string kind, CancellationToken ct)
    {
        var result = new List<DiscoveryResult>();
        if (kind == "channels")
        {
            await foreach (var c in _youtube.Search.GetChannelsAsync(query, ct))
            { result.Add(new(kind, c.Id, c.Title, "Channel")); if (result.Count == 20) break; }
        }
        else if (kind == "playlists")
        {
            await foreach (var p in _youtube.Search.GetPlaylistsAsync(query, ct))
            { result.Add(new(kind, p.Id, p.Title, p.Author?.ChannelTitle ?? "Playlist")); if (result.Count == 20) break; }
        }
        else throw new System.ComponentModel.DataAnnotations.ValidationException("Invalid discovery type.");
        return result;
    }
    private static int PageOffset(string? page)
    {
        if (page is null) return 0;
        if (int.TryParse(page, out var offset) && offset is > 0 and < 500 && offset % 20 == 0) return offset;
        throw new System.ComponentModel.DataAnnotations.ValidationException("Invalid video page.");
    }
    public async Task<CatalogPage> PlaylistVideos(string id, CancellationToken ct, string? page = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{10,100}$")) throw new System.ComponentModel.DataAnnotations.ValidationException("Invalid playlist ID.");
        var result = new List<VideoInfo>();
        var offset = PageOffset(page);
        var remaining = offset;
        await foreach (var video in _youtube.Playlists.GetVideosAsync(id, ct))
        {
            if (remaining-- > 0) continue;
            result.Add(new(video.Id, video.Title, video.Author.ChannelTitle, video.Author.ChannelId,
                "https://i.ytimg.com/vi/" + video.Id + "/hqdefault.jpg", video.Duration?.TotalSeconds));
            if (result.Count == 21) break;
        }
        return BoundedPage(result, offset);
    }
    private static CatalogPage BoundedPage(List<VideoInfo> items, int offset) =>
        new(items.Take(20).ToList(), items.Count > 20 && offset + 20 < 500 ? (offset + 20).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);

    public async Task<CatalogPage> Uploads(string id, CancellationToken ct, string? page = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^UC[A-Za-z0-9_-]{22}$")) throw new System.ComponentModel.DataAnnotations.ValidationException("Invalid channel ID.");
        var offset = PageOffset(page);
        var key = id + ":" + offset;
        if (_uploads.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Page;
        var result = new List<VideoInfo>();
        var remaining = offset;
        await foreach (var video in _youtube.Channels.GetUploadsAsync(id, ct))
        {
            if (remaining-- > 0) continue;
            result.Add(new(video.Id, video.Title, video.Author.ChannelTitle, video.Author.ChannelId,
                "https://i.ytimg.com/vi/" + video.Id + "/hqdefault.jpg", video.Duration?.TotalSeconds));
            if (result.Count == 21) break;
        }
        var resultPage = BoundedPage(result, offset);
        if (_uploads.Count >= 100) foreach (var old in _uploads.OrderBy(x => x.Value.Expires).Take(20)) _uploads.TryRemove(old.Key, out _);
        _uploads[key] = (DateTimeOffset.UtcNow.AddMinutes(15), resultPage); return resultPage;
    }
    public async Task Download(VideoInfo video, string directory, int height, JellyfinMuxer muxer, CancellationToken ct, Action<double, string>? report = null)
    {
        Rules.VideoId(video.Id); Directory.CreateDirectory(directory);
        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id, ct);
            var v = manifest.GetVideoOnlyStreams().Where(s => s.VideoResolution.Height <= height && s.Container.Name == "mp4")
                .OrderByDescending(s => s.VideoCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.VideoResolution.Height).ThenByDescending(s => s.Bitrate).FirstOrDefault();
            var a = manifest.GetAudioOnlyStreams().Where(s => s.Container.Name == "mp4").OrderByDescending(s => s.Bitrate).FirstOrDefault();
            if (v is not null && a is not null)
            {
                if (!AllowedMediaUri(v.Url) || !AllowedMediaUri(a.Url)) throw new ProviderException("The provider returned an unsupported download destination.");
                if (v.Size.Bytes + a.Size.Bytes > 8L * 1024 * 1024 * 1024) throw new ProviderException("This download exceeds the 8 GiB item limit.");
                var fraction = v.Size.Bytes / (double)(v.Size.Bytes + a.Size.Bytes);
                await _youtube.Videos.Streams.DownloadAsync(v, Path.Combine(directory, "video-track.mp4"), new InlineProgress(p => report?.Invoke(p * fraction * .95, "Downloading video")), ct);
                await _youtube.Videos.Streams.DownloadAsync(a, Path.Combine(directory, "audio-track.m4a"), new InlineProgress(p => report?.Invoke((fraction + p * (1 - fraction)) * .95, "Downloading audio")), ct);
                report?.Invoke(.96, "Merging audio and video");
                await muxer.Merge(Path.Combine(directory, "video-track.mp4"), Path.Combine(directory, "audio-track.m4a"), Path.Combine(directory, "video.mp4"), ct);
                File.Delete(Path.Combine(directory, "video-track.mp4")); File.Delete(Path.Combine(directory, "audio-track.m4a"));
            }
            else
            {
                var combined = manifest.GetMuxedStreams().Where(s => s.VideoResolution.Height <= height && s.Container.Name == "mp4")
                    .OrderByDescending(s => s.VideoQuality.MaxHeight).FirstOrDefault() ?? throw new ProviderException("No compatible formats are available.");
                if (!AllowedMediaUri(combined.Url)) throw new ProviderException("The provider returned an unsupported download destination.");
                if (combined.Size.Bytes > 8L * 1024 * 1024 * 1024) throw new ProviderException("This download exceeds the 8 GiB item limit.");
                await _youtube.Videos.Streams.DownloadAsync(combined, Path.Combine(directory, "video.mp4"), new InlineProgress(p => report?.Invoke(p * .95, "Downloading video")), ct);
            }
        }
        catch (Exception ex) when (ex is not (ProviderException or OperationCanceledException))
        { throw new ProviderException("The managed downloader could not complete this video. Retry later or update JellyTube."); }
    }
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    internal static string Text(JsonElement e, string property, string fallback = "") => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
}
