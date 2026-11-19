using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>
/// YoutubeExplode validates the final two bytes of every advertised format before returning its manifest.
/// Trust declared lengths during discovery; Extractor validates the selected audio/video against real media.
/// Actual media downloads and any request outside that exact metadata-probe shape are forwarded unchanged.
/// </summary>
internal sealed class ManifestHttpHandler(HttpClient upstream) : HttpMessageHandler
{
    internal static bool TryLengthProbe(HttpRequestMessage request, out long size)
    {
        size = 0;
        if (request.Headers.Range is not null || request.Method != HttpMethod.Get || request.RequestUri is not { } uri || !Extractor.AllowedMediaUri(uri.AbsoluteUri)) return false;
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("clen", out var declared) || !long.TryParse(declared.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out size)
            || size < 3 || !query.TryGetValue("range", out var range)) return false;
        return range.ToString() == FormattableString.Invariant($"{size - 2}-{size - 1}");
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryLengthProbe(request, out var size))
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            { Version = request.Version, VersionPolicy = request.VersionPolicy, Content = request.Content };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await upstream.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        Performance.Record("playback.unselectedProbe.skipped", 0);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request, Content = new ByteArrayContent(new byte[2]) };
        response.Content.Headers.ContentRange = new(size - 2, size - 1, size);
        return response;
    }
}
