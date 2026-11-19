using System.Net;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Follows bounded CDN redirects without permitting arbitrary destinations or losing byte ranges.</summary>
internal sealed class MediaRedirectHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            throw new ProviderException("Unsupported media request.");
        var uri = request.RequestUri ?? throw new ProviderException("Missing media destination.");
        for (var hop = 0; ; hop++)
        {
            if (!Extractor.AllowedMediaUri(uri.AbsoluteUri)) throw new ProviderException("Unsupported media destination.");
            using var next = new HttpRequestMessage(request.Method, uri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            // Only media negotiation headers travel between hosts. Never forward authentication or cookies.
            foreach (var name in new[] { "Range", "If-Range", "Accept", "User-Agent" })
                if (request.Headers.TryGetValues(name, out var values)) next.Headers.TryAddWithoutValidation(name, values);
            var response = await base.SendAsync(next, ct);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (hop >= 3 || location is null) throw new ProviderException("YouTube returned an invalid media redirect.", "stream-redirect", true);
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }
}
