using System.Net;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Capability-bound media transport for Jellyfin/FFmpeg/native clients, including byte-range seeking.</summary>
[ApiController, Route("JellyTube/Media/{owner}/{id}/{secret}"), AllowAnonymous, JellyTubeErrors, JellyTubeTiming]
public sealed class MediaController(Store store, NativeLibrary native, Extractor extractor, IHttpClientFactory http, SegmentCache cache, Downloads downloads, ThumbnailCache thumbnails,
    Microsoft.Extensions.Logging.ILogger<MediaController> logger) : ControllerBase
{
    internal const int SharedStreamHeight = 1080;
    private static bool HasSharedQuality(string owner) => owner == NativeLibrary.SharedOwner || owner.StartsWith(NativeLibrary.DirectPrefix, StringComparison.Ordinal);
    private int Height(string owner) => owner.StartsWith(NativeLibrary.DirectPrefix, StringComparison.Ordinal)
        ? store.Settings(owner[NativeLibrary.DirectPrefix.Length..]).MaxHeight
        : owner == NativeLibrary.SharedOwner ? SharedStreamHeight : store.Settings(owner).MaxHeight;
    [HttpGet("~/JellyTube/Thumbnails/{id}.jpg")]
    public async Task<ActionResult> Thumbnail(string id, CancellationToken ct)
    {
        var bytes = await thumbnails.Get(id, ct);
        if (bytes is null)
        {
            Response.Headers.CacheControl = "no-store";
            return NotFound();
        }
        var etag = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) + "\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=21600";
        if (Request.Headers.IfNoneMatch.Any(value => value == etag)) return StatusCode(304);
        return File(bytes, "image/jpeg");
    }
    [HttpGet("manifest.mpd"), HttpHead("manifest.mpd")]
    public async Task<ActionResult> Manifest(string owner, string id, string secret, CancellationToken ct, [FromQuery] bool web = false)
    {
        var asset = native.Resolve(owner, id, secret);
        Response.Headers.CacheControl = "private, no-store";
        var height = Height(owner);
        if (!web && downloads.PlaybackFile(id, height, asset) is not null)
            return Redirect(Plugin.Instance.Configuration.PublicBaseUrl.TrimEnd('/') + "/JellyTube/Media/" + owner + "/" + id + "/" + secret + "/local.mp4");
        var seconds = asset.Video.DurationSeconds;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var prefix = web ? "./" : Plugin.Instance.Configuration.PublicBaseUrl.TrimEnd('/') + "/JellyTube/Media/" + owner + "/" + id + "/" + secret + "/";
        var source = await extractor.ResolvePlayback(id, height, ct);
        if (source.Progressive is { } progressive)
            return Redirect(prefix + "progressive.mp4?identity=" + progressive.Identity);
        if (seconds is null or <= 0) seconds = (await extractor.Details(id, ct)).DurationSeconds;
        if (seconds is null or <= 0) throw new ProviderException("Live or unknown-duration videos are not supported in this preview.");
        logger.LogInformation("JellyTube media request {Reference} prepared the streaming manifest in {ResolutionMs:F0} ms.",
            HttpContext.TraceIdentifier, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var xml = ManifestXml(source.Adaptive!, seconds.Value, prefix, height, web);
        return Content(xml, "application/dash+xml");
    }
    internal static string ManifestXml(AdaptiveSource source, double seconds, string prefix, int height = 720, bool web = false)
    {
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        string TrackUrl(AdaptiveTrack track, string kind) => prefix + kind + ".mp4?height=" + height + "&identity=" + Fingerprint(track);
        XElement Segments(AdaptiveTrack track, string kind)
        {
            if (track.Segments is null || track.Segments.Count == 0)
                return new(ns + "SegmentBase", new XAttribute("indexRange", $"{track.IndexStart}-{track.IndexEnd}"), new XElement(ns + "Initialization", new XAttribute("range", "0-" + track.InitEnd)));
            var timeline = new XElement(ns + "SegmentTimeline");
            var time = track.PresentationTime;
            foreach (var segment in track.Segments) { timeline.Add(new XElement(ns + "S", new XAttribute("t", time), new XAttribute("d", segment.Duration))); time += segment.Duration; }
            return new(ns + "SegmentList", new XAttribute("timescale", track.Timescale), new XAttribute("presentationTimeOffset", track.PresentationTime),
                new XElement(ns + "Initialization", new XAttribute("sourceURL", TrackUrl(track, kind)), new XAttribute("range", "0-" + track.InitEnd)), timeline,
                track.Segments.Select(s => new XElement(ns + "SegmentURL", new XAttribute("media", TrackUrl(track, kind)), new XAttribute("mediaRange", $"{s.Start}-{s.End}"))));
        }
        XElement Adaptation(AdaptiveTrack track, string kind) => new(ns + "AdaptationSet", new XAttribute("contentType", kind),
            new XAttribute("mimeType", kind + "/mp4"), new XAttribute("segmentAlignment", "true"),
            new XElement(ns + "Representation", new XAttribute("id", kind), new XAttribute("bandwidth", Math.Max(1, track.Bitrate)),
                new XAttribute("codecs", track.Codec), kind == "video" ? new object[] { new XAttribute("width", track.Width), new XAttribute("height", track.Height) } : null,
                new XElement(ns + "BaseURL", TrackUrl(track, kind)),
                Segments(track, kind)));
        return new XDocument(new XElement(ns + "MPD", new XAttribute("type", "static"), new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-on-demand:2011"),
            new XAttribute("minBufferTime", web ? "PT1S" : "PT3S"), new XAttribute("mediaPresentationDuration", XmlConvert.ToString(TimeSpan.FromSeconds(seconds))),
            new XElement(ns + "Period", new XAttribute("start", "PT0S"), Adaptation(source.Video, "video"), Adaptation(source.Audio, "audio")))).ToString();
    }
    internal static string Fingerprint(AdaptiveTrack track) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{track.Codec}|{track.Size}|{track.Width}|{track.Height}|{track.IndexEnd}|{track.IndexIdentity}")))[..24].ToLowerInvariant();
    [HttpGet("local.mp4"), HttpHead("local.mp4")]
    public ActionResult Local(string owner, string id, string secret, [FromQuery] bool fullQuality = false)
    {
        var asset = native.Resolve(owner, id, secret);
        var height = Height(owner);
        var local = downloads.OpenPlayback(id, height, asset, Response, fullQuality ? height : 0);
        if (local is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return File(local, "video/mp4", enableRangeProcessing: true);
    }
    [HttpGet("{track:regex(^(video|audio|progressive)$)}.mp4"), HttpHead("{track:regex(^(video|audio|progressive)$)}.mp4")]
    public async Task Stream(string owner, string id, string secret, string track, [FromQuery] int? height, [FromQuery] string? identity, CancellationToken ct)
    {
        native.Resolve(owner, id, secret); var selectedHeight = owner.StartsWith(NativeLibrary.DirectPrefix, StringComparison.Ordinal) || owner == NativeLibrary.SharedOwner ? Height(owner) : height ?? Height(owner);
        if (selectedHeight is not (360 or 480 or 720 or 1080 or 1440 or 2160)) throw new System.ComponentModel.DataAnnotations.ValidationException("Invalid media quality.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(30));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var source = await extractor.ResolvePlayback(id, selectedHeight, deadline.Token);
            var adaptive = track == "video" ? source.Adaptive?.Video : source.Adaptive?.Audio;
            var progressive = track == "progressive" ? source.Progressive : null;
            if (track == "progressive" ? progressive is null : adaptive is null)
                throw new ProviderException("The upstream media format changed. Restart playback to refresh it.");
            var url = progressive?.Url ?? adaptive!.Url;
            var size = progressive?.Size ?? adaptive!.Size;
            if (!Extractor.AllowedMediaUri(url)) throw new ProviderException("Invalid upstream media host.");
            var fingerprint = progressive?.Identity ?? Fingerprint(adaptive!);
            if (identity is not null && identity != fingerprint) throw new ProviderException("The upstream media format changed. Restart playback to refresh it.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (Request.Headers.Range.Count > 0)
            {
                if (!System.Net.Http.Headers.RangeHeaderValue.TryParse(Request.Headers.Range.ToString(), out var range) || range.Unit != "bytes" || range.Ranges.Count != 1)
                { Response.StatusCode = 416; return; }
                request.Headers.Range = range;
            }
            var single = request.Headers.Range?.Ranges.SingleOrDefault();
            if (single is not null)
            {
                var fromByte = single.From ?? Math.Max(0, size - single.To!.Value);
                var toByte = single.From is null ? size - 1 : Math.Min(single.To ?? Math.Min(size - 1, fromByte + 9_898_988), size - 1);
                if (fromByte >= size || toByte < fromByte || single.From is null && single.To == 0)
                { Response.StatusCode = 416; Response.Headers.ContentRange = $"bytes */{size}"; return; }
                single = new System.Net.Http.Headers.RangeItemHeaderValue(fromByte, toByte);
                // Googlevideo's native range query is also used by the managed downloader.
                request.RequestUri = new Uri(Extractor.MediaRangeUri(url, fromByte, toByte));
                request.Headers.Range = null;
            }
            if (HttpMethods.IsHead(Request.Method))
            {
                Response.ContentType = track == "audio" ? "audio/mp4" : "video/mp4";
                Response.Headers.AcceptRanges = "bytes"; Response.Headers.CacheControl = "private, no-store";
                Response.StatusCode = single is null ? 200 : 206;
                Response.ContentLength = single is null ? size : single.To!.Value - single.From!.Value + 1;
                if (single is not null) Response.Headers.ContentRange = $"bytes {single.From}-{single.To}/{size}";
                return;
            }
            var cacheKey = SegmentCache.Key(id, track, fingerprint, single?.From, single?.To);
            using var fill = !HttpMethods.IsHead(Request.Method) && single?.From is { } start && single.To is { } end
                && end >= start && end - start < 4 * 1024 * 1024
                ? await cache.EnterFill(cacheKey, deadline.Token) : null;
            if (single?.From is { } from && single.To is { } to && to >= from && cache.Get(cacheKey) is { } cached)
            {
                Response.StatusCode = 206; Response.ContentType = track == "audio" ? "audio/mp4" : "video/mp4";
                Response.Headers.CacheControl = "private, no-store"; Response.Headers.AcceptRanges = "bytes";
                Response.Headers.ContentRange = $"bytes {from}-{to}/{size}"; Response.ContentLength = cached.Length;
                if (!HttpMethods.IsHead(Request.Method)) await Response.Body.WriteAsync(cached, ct); return;
            }
            using var transport = await cache.Enter(deadline.Token, single?.To is not null);
            using var timing = Performance.Measure("media.upstream");
            var mediaStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using var response = await http.CreateClient("JellyTube.Media").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone && attempt == 0)
            { extractor.InvalidateAdaptive(id, selectedHeight); continue; }
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { Response.StatusCode = 416; return; }
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) throw new ProviderException("YouTube could not serve this media range. Retry playback.");
            if (single is not null)
            {
                var contentRange = response.Content.Headers.ContentRange;
                if (response.Content.Headers.ContentLength != single.To - single.From + 1
                    || response.StatusCode == HttpStatusCode.PartialContent
                        && (contentRange is null || contentRange.From != single.From || contentRange.To != single.To || contentRange.Length != size))
                    throw new ProviderException("YouTube returned an incompatible media range. Restart playback.");
                Response.StatusCode = 206;
                Response.Headers.ContentRange = $"bytes {single.From}-{single.To}/{size}";
            }
            else Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = track == "audio" ? "audio/mp4" : "video/mp4";
            Response.Headers.CacheControl = "private, no-store"; Response.Headers.AcceptRanges = "bytes";
            Response.ContentLength = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            if (Response.StatusCode == 206 && single?.From is { } first && single.To is { } last && fill is not null)
            {
                var bytes = new byte[(int)(last - first + 1)];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = await input.ReadAsync(bytes.AsMemory(offset, Math.Min(64 * 1024, bytes.Length - offset)), deadline.Token);
                    if (read == 0) throw new IOException("The upstream media range ended early.");
                    await Response.Body.WriteAsync(bytes.AsMemory(offset, read), deadline.Token);
                    if (offset == 0) { Performance.Record("media.firstByte", System.Diagnostics.Stopwatch.GetElapsedTime(mediaStart).TotalMilliseconds); await Response.Body.FlushAsync(deadline.Token); }
                    offset += read;
                }
                cache.Put(cacheKey, bytes);
                return;
            }
            await input.CopyToAsync(Response.Body, deadline.Token); return;
        }
    }
}
