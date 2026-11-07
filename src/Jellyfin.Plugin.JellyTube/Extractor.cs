using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    private readonly ConcurrentDictionary<string, Lazy<Task<PlaybackSource>>> _adaptivePending = new();
    private readonly ConcurrentDictionary<string, PlaybackSource> _adaptiveCache = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, CatalogPage Page)> _uploads = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, VideoInfo Video)> _details = new();
    private readonly IHttpClientFactory _http;
    private readonly SegmentCache _segments;
    private readonly ILogger<Extractor> _logger;
    public Extractor(IHttpClientFactory http, SegmentCache segments, ILogger<Extractor> logger)
    { _http = http; _segments = segments; _logger = logger; _youtube = new(http.CreateClient("JellyTube.YouTube")); }
    public async Task<PlaybackSource> ResolvePlayback(string id, int maxHeight, CancellationToken ct)
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
        finally { if (pending.IsValueCreated && pending.Value.IsCompleted) _adaptivePending.TryRemove(new KeyValuePair<string, Lazy<Task<PlaybackSource>>>(key, pending)); }
    }
    public void InvalidateAdaptive(string id, int height) => _adaptiveCache.TryRemove(id + ":" + height, out _);
    internal async Task WarmAdaptive(string id, int height)
    {
        try { await ResolvePlayback(id, height, CancellationToken.None); }
        catch (Exception ex) when (ex is ProviderException or OperationCanceledException)
        {
            // Preparation is best effort. The manifest request can retry and report playback errors.
        }
    }
    private async Task<PlaybackSource> AdaptiveCore(string id, int height)
    {
        var queued = System.Diagnostics.Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _slots.WaitAsync(timeout.Token);
        try
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var manifest = await ResolveFormats(id, timeout.Token);
            var indexing = System.Diagnostics.Stopwatch.GetTimestamp();
            var video = manifest.GetVideoOnlyStreams().Where(s => s.Container.Name == "mp4" && s.VideoResolution.Height <= height)
                .OrderByDescending(s => s.VideoCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.VideoQuality.Framerate is > 0 and <= 30)
                .ThenByDescending(s => s.VideoResolution.Height).ThenByDescending(s => s.Bitrate).FirstOrDefault();
            var audio = PreferOriginalAudio(manifest.GetAudioOnlyStreams().Where(s => s.Container.Name == "mp4"))
                .ThenByDescending(s => s.Bitrate).FirstOrDefault();
            var progressive = PreferOriginalAudio(manifest.GetMuxedStreams().Where(s => s.Container.Name == "mp4"
                    && s.VideoCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)
                    && s.AudioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)
                    && s.VideoResolution.Height <= height && s.Size.Bytes > 0 && AllowedMediaUri(s.Url)
                    && (video is null || audio is null || (s.VideoResolution.Height >= video.VideoResolution.Height
                        && (video.VideoQuality.Framerate > 30 || s.VideoQuality.Framerate is > 0 and <= 30)))
                    && (audio is null || CompatibleAudio(s, audio))))
                .ThenByDescending(s => s.VideoQuality.Framerate is > 0 and <= 30)
                .ThenByDescending(s => s.VideoResolution.Height).ThenByDescending(s => s.Bitrate).FirstOrDefault();
            var expires = DateTimeOffset.UtcNow.AddMinutes(5);
            if (progressive is not null)
            {
                var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    $"{progressive.Size.Bytes}|{progressive.VideoCodec}|{progressive.AudioCodec}|{progressive.AudioLanguage?.Name}|{progressive.VideoResolution}")))[..24];
                _logger.LogInformation("JellyTube selected an unchanged MP4 in {FormatsMs:F0} ms. Audio language {Language}.",
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, progressive.AudioLanguage?.Code ?? "unknown");
                return new(null, new(progressive.Url, progressive.Size.Bytes, identity), expires);
            }
            if (video is null || audio is null) throw new ProviderException("No supported audio and video combination is available.");
            var videoIndex = IndexTrack(id, "video", video, video.VideoCodec, video.VideoResolution.Width, video.VideoResolution.Height, timeout.Token);
            var audioIndex = IndexTrack(id, "audio", audio, audio.AudioCodec, 0, 0, timeout.Token);
            await Task.WhenAll(videoIndex, audioIndex);
            _logger.LogInformation("JellyTube stream lookup waited {QueueMs:F0} ms for capacity, resolved YouTube formats in {FormatsMs:F0} ms, and read media indexes in {IndexMs:F0} ms. Audio language {Language}, original marker {Original}.",
                System.Diagnostics.Stopwatch.GetElapsedTime(queued, started).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(started, indexing).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(indexing).TotalMilliseconds,
                audio.AudioLanguage?.Code ?? "unknown", AudioPreference(audio.AudioLanguage?.Name, audio.IsAudioLanguageDefault, audio.Url) == 4);
            return new(new(await videoIndex, await audioIndex, expires), null, expires);
        }
        catch (Exception ex) when (ex is not (ProviderException or OperationCanceledException))
        { throw new ProviderException("Managed YouTube extraction failed. The video may be restricted or the extractor needs an update."); }
        finally { _slots.Release(); }
    }
    private async Task<StreamManifest> ResolveFormats(string id, CancellationToken ct)
    {
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = _youtube.Videos.Streams.GetManifestAsync(id, attempts.Token).AsTask();
        var delay = Task.Delay(TimeSpan.FromSeconds(5), attempts.Token);
        try
        {
            if (await Task.WhenAny(first, delay) == first) return await first;
            ct.ThrowIfCancellationRequested();
            // A slow upstream request can hold Jellyfin's probe open. Allow one overlapping retry.
            var second = _youtube.Videos.Streams.GetManifestAsync(id, attempts.Token).AsTask();
            var winner = await Task.WhenAny(first, second);
            var other = winner == first ? second : first;
            _ = other.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { return await winner; }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested) { return await other; }
        }
        finally { attempts.Cancel(); }
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
            var stream = PreferOriginalAudio(manifest.GetMuxedStreams().Where(s => s.VideoResolution.Height <= height))
                .ThenByDescending(s => s.Container.Name == "mp4").ThenByDescending(s => s.VideoQuality.MaxHeight).FirstOrDefault();
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

    internal static int AudioPreference(string? name, bool? isDefault, string url)
    {
        var tags = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("xtags", out var value)
            ? value.ToString().Split(':')
            : [];
        var label = name ?? "";
        if (label.Contains("audio description", StringComparison.OrdinalIgnoreCase)
            || label.Contains("descriptive", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("acont=descriptive", StringComparer.OrdinalIgnoreCase)) return 0;
        if (tags.Contains("acont=original", StringComparer.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(label, @"\boriginal\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return 4;
        if (tags.Contains("acont=dubbed", StringComparer.OrdinalIgnoreCase)
            || label.Contains("dubbed", StringComparison.OrdinalIgnoreCase)) return 1;
        return isDefault == true ? 3 : 2;
    }

    private static bool CompatibleAudio(IAudioStreamInfo combined, IAudioStreamInfo separate)
    {
        var combinedRank = AudioPreference(combined.AudioLanguage?.Name, combined.IsAudioLanguageDefault, combined.Url);
        var separateRank = AudioPreference(separate.AudioLanguage?.Name, separate.IsAudioLanguageDefault, separate.Url);
        return combinedRank >= separateRank && (separate.AudioLanguage is null
            || string.Equals(combined.AudioLanguage?.Code, separate.AudioLanguage?.Code, StringComparison.OrdinalIgnoreCase));
    }

    private static IOrderedEnumerable<T> PreferOriginalAudio<T>(IEnumerable<T> streams) where T : IAudioStreamInfo =>
        streams.OrderByDescending(stream => AudioPreference(stream.AudioLanguage?.Name, stream.IsAudioLanguageDefault, stream.Url))
            .ThenByDescending(stream => stream.AudioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase));
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
            _details[id] = (DateTimeOffset.UtcNow.AddHours(6), details);
            return details;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ProviderException("Video details are unavailable. The video may be private or restricted."); }
    }
    internal CatalogPage WithCachedDates(CatalogPage page)
    {
        var now = DateTimeOffset.UtcNow;
        return page with
        {
            Items = page.Items.Select(video => _details.TryGetValue(video.Id, out var cached) && cached.Expires > now
                ? video with { PublishedAt = video.PublishedAt ?? cached.Video.PublishedAt, DurationSeconds = video.DurationSeconds ?? cached.Video.DurationSeconds }
                : video).ToList()
        };
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
            var a = PreferOriginalAudio(manifest.GetAudioOnlyStreams().Where(s => s.Container.Name == "mp4"))
                .ThenByDescending(s => s.Bitrate).FirstOrDefault();
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
                var combined = PreferOriginalAudio(manifest.GetMuxedStreams().Where(s => s.VideoResolution.Height <= height && s.Container.Name == "mp4"))
                    .ThenByDescending(s => s.VideoQuality.MaxHeight).FirstOrDefault() ?? throw new ProviderException("No compatible formats are available.");
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
