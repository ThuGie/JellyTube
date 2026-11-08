using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.ClosedCaptions;
using YoutubeExplode.Videos.Streams;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Reads complete, unciphered MP4 formats from a single YouTube player response.</summary>
public sealed class QuickFormats(IHttpClientFactory clients) : IDisposable
{
    private readonly SemaphoreSlim _visitorGate = new(1);
    private volatile Visitor? _visitor;
    private sealed record Visitor(string Value, DateTimeOffset Expires);

    public async Task<StreamManifest?> TryGet(string id, int height, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var client = clients.CreateClient("JellyTube.YouTube");
        try
        {
            var visitor = await GetVisitor(client, deadline.Token);
            if (visitor is null) return null;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player")
            {
                Content = JsonContent.Create(new
                {
                    videoId = id,
                    context = new { client = new
                    {
                        clientName = "VISIONOS", clientVersion = "1.02", deviceMake = "Apple",
                        deviceModel = "RealityDevice17,1", osName = "visionOS", osVersion = "26.5.23O471",
                        visitorData = visitor, hl = "en", gl = "US", utcOffsetMinutes = 0
                    } }
                })
            };
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15");
            using var response = await client.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            return Parse(json.RootElement, id, height);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
            or ArgumentException or IndexOutOfRangeException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<string?> GetVisitor(HttpClient client, CancellationToken ct)
    {
        if (_visitor is { } known && known.Expires > DateTimeOffset.UtcNow) return known.Value;
        await _visitorGate.WaitAsync(ct);
        try
        {
            if (_visitor is { } cached && cached.Expires > DateTimeOffset.UtcNow) return cached.Value;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.youtube/20.10.38 (Linux; U; ANDROID 11) gzip");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(text.StartsWith(")]}'", StringComparison.Ordinal) ? text[4..] : text);
            var value = json.RootElement[0][2][0][0][13].GetString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            _visitor = new(value, DateTimeOffset.UtcNow.AddHours(6));
            return value;
        }
        finally { _visitorGate.Release(); }
    }

    internal static StreamManifest? Parse(JsonElement root, string id, int height)
    {
        if (!root.TryGetProperty("playabilityStatus", out var status) || Extractor.Text(status, "status") != "OK"
            || !root.TryGetProperty("videoDetails", out var details) || Extractor.Text(details, "videoId") != id
            || details.TryGetProperty("isLive", out var live) && live.ValueKind == JsonValueKind.True
            || !root.TryGetProperty("streamingData", out var data)
            || !data.TryGetProperty("adaptiveFormats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return null;
        if (data.TryGetProperty("formats", out var combined) && combined.ValueKind == JsonValueKind.Array
            && combined.EnumerateArray().Any(format => Number(format, "height") >= height
                && Extractor.Text(format, "mimeType").StartsWith("video/mp4;", StringComparison.Ordinal)))
            return null;

        var streams = new List<IStreamInfo>();
        var bestHeight = 0;
        var availableHeight = 0;
        var bestAudio = -1;
        var availableAudio = -1;
        foreach (var format in formats.EnumerateArray())
        {
            var mime = Extractor.Text(format, "mimeType");
            var audio = mime.StartsWith("audio/mp4;", StringComparison.Ordinal);
            var video = mime.StartsWith("video/mp4;", StringComparison.Ordinal);
            var formatHeight = Number(format, "height");
            if (!audio && (!video || formatHeight <= 0 || formatHeight > height)) continue;
            var url = Extractor.Text(format, "url");
            var codec = mime.Split('"').ElementAtOrDefault(1);
            var languageCode = "";
            var languageName = "";
            bool? isDefault = null;
            if (format.TryGetProperty("audioTrack", out var track) && track.ValueKind == JsonValueKind.Object)
            {
                languageCode = Extractor.Text(track, "id").Split('.')[0];
                languageName = Extractor.Text(track, "displayName");
                if (track.TryGetProperty("audioIsDefault", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    isDefault = flag.GetBoolean();
            }
            var rank = audio ? Extractor.AudioPreference(languageName, isDefault, url) : -1;
            if (audio) availableAudio = Math.Max(availableAudio, rank);
            else availableHeight = Math.Max(availableHeight, formatHeight);
            if (!Extractor.AllowedMediaUri(url) || QueryHelpers.ParseQuery(new Uri(url).Query).ContainsKey("n")
                || string.IsNullOrWhiteSpace(codec) || !long.TryParse(Extractor.Text(format, "contentLength"), out var length)
                || length <= 0 || Number(format, "bitrate") <= 0) continue;

            var size = new FileSize(length);
            var bitrate = new Bitrate(Number(format, "bitrate"));
            if (audio)
            {
                Language? language = languageCode.Length > 0 ? new Language(languageCode, languageName) : null;
                streams.Add(new AudioOnlyStreamInfo(url, Container.Mp4, size, bitrate, codec, language, isDefault));
                bestAudio = Math.Max(bestAudio, rank);
            }
            else if (Number(format, "width") is > 0 and var width)
            {
                streams.Add(new VideoOnlyStreamInfo(url, Container.Mp4, size, bitrate, codec,
                    new VideoQuality(formatHeight, Number(format, "fps")), new Resolution(width, formatHeight), false));
                bestHeight = Math.Max(bestHeight, formatHeight);
            }
        }
        var languages = streams.OfType<IAudioStreamInfo>().Select(stream => stream.AudioLanguage?.Code)
            .Where(code => !string.IsNullOrEmpty(code)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count();
        return bestHeight > 0 && bestHeight == availableHeight && bestAudio >= 2 && bestAudio == availableAudio
            && (languages < 2 || bestAudio == 4) ? new StreamManifest(streams) : null;
    }

    private static int Number(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : 0;

    public void Dispose() => _visitorGate.Dispose();
}
