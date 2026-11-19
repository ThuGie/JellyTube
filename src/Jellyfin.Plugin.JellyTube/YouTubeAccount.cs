using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.JellyTube;

public sealed class YouTubeAccount(Store store, IHttpClientFactory clients, Access access)
{
    private const string Scope = "https://www.googleapis.com/auth/youtube.readonly";
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly Lazy<IDataProtector> _protector = new(() => DataProtectionProvider.Create(
        new DirectoryInfo(Path.Combine(Plugin.Instance.DataFolderPath, "oauth-keys"))).CreateProtector("JellyTube.YouTubeAccount.v1"));
    private sealed record Pending(string Owner, string Verifier, string Redirect, string ClientId, DateTimeOffset Expires);
    private sealed record Connection(string ClientId, string ProtectedToken, DateTimeOffset SyncedAt, List<ChannelInfo> Channels);
    private sealed record Token(string Access, string Refresh);
    private sealed record UploadSnapshot(DateTimeOffset CreatedAt, CatalogPage Page);
    private readonly ConcurrentDictionary<string, (string ProtectedRefresh, Token Token, DateTimeOffset Expires)> _tokens = new();
    private readonly SemaphoreSlim _uploadsGate = new(4);

    public async Task<CatalogPage> Uploads(string owner, string channel, string? page, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(channel, "^UC[A-Za-z0-9_-]{22}$")
            || (page?.Length ?? 0) > 512) throw new System.ComponentModel.DataAnnotations.ValidationException("Invalid channel or page.");
        var key = channel + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(page ?? "")))[..16];
        if (!Connected(owner)) throw new InvalidOperationException("Connect YouTube first.");
        var cached = store.Get<UploadSnapshot>(owner, "youtubeUploads", key);
        if (cached is not null && cached.CreatedAt.AddMinutes(15) > DateTimeOffset.UtcNow) return cached.Page;
        await _uploadsGate.WaitAsync(ct);
        try
        {
            cached = store.Get<UploadSnapshot>(owner, "youtubeUploads", key);
            if (cached is not null && cached.CreatedAt.AddMinutes(15) > DateTimeOffset.UtcNow) return cached.Page;
            var gate = Gate(owner);
            await gate.WaitAsync(ct);
            Token token;
            Connection connection;
            try
            {
                connection = store.Get<Connection>(owner, "youtubeConnection", "current")
                    ?? throw new InvalidOperationException("Connect YouTube first.");
                if (!Configured || connection.ClientId != Plugin.Instance.Configuration.YouTubeClientId)
                    throw new InvalidOperationException("YouTube setup changed. Reconnect your account.");
                if (_tokens.TryGetValue(owner, out var known) && known.ProtectedRefresh == connection.ProtectedToken
                    && known.Expires > DateTimeOffset.UtcNow) token = known.Token;
                else
                {
                    token = await Exchange(new() { ["refresh_token"] = _protector.Value.Unprotect(connection.ProtectedToken),
                        ["grant_type"] = "refresh_token" }, ct);
                    if (_tokens.Count >= 128) _tokens.Clear();
                    _tokens[owner] = (connection.ProtectedToken, token, DateTimeOffset.UtcNow.AddMinutes(5));
                }
            }
            finally { gate.Release(); }
            using var channels = await ReadApi("channels?part=contentDetails&id=" + Uri.EscapeDataString(channel), token.Access, ct);
            var channelItem = channels.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault();
            if (channelItem.ValueKind == JsonValueKind.Undefined) return new([]);
            var playlist = Extractor.Text(channelItem.GetProperty("contentDetails").GetProperty("relatedPlaylists"), "uploads");
            if (playlist.Length == 0) return new([]);
            using var uploads = await ReadApi("playlistItems?part=contentDetails&maxResults=20&playlistId=" + Uri.EscapeDataString(playlist)
                + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), token.Access, ct);
            var ids = uploads.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => Extractor.Text(item.GetProperty("contentDetails"), "videoId")).Where(id => id.Length == 11).Distinct().ToArray();
            var videos = new List<VideoInfo>();
            if (ids.Length > 0)
            {
                using var metadata = await ReadApi("videos?part=snippet,contentDetails&id=" + string.Join(',', ids), token.Access, ct);
                foreach (var item in metadata.RootElement.GetProperty("items").EnumerateArray())
                {
                    var id = Rules.VideoId(Extractor.Text(item, "id"));
                    var snippet = item.GetProperty("snippet");
                    double? duration = null;
                    try { duration = System.Xml.XmlConvert.ToTimeSpan(Extractor.Text(item.GetProperty("contentDetails"), "duration")).TotalSeconds; }
                    catch (FormatException) { }
                    videos.Add(new(id, Extractor.Text(snippet, "title"), Extractor.Text(snippet, "channelTitle"),
                        Extractor.Text(snippet, "channelId"), $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                        duration, Extractor.Text(snippet, "description"),
                        DateTimeOffset.TryParse(Extractor.Text(snippet, "publishedAt"), out var published) ? published : null,
                        snippet.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(tag => tag.GetString() ?? "").Take(30).ToArray() : []));
                }
            }
            var next = Extractor.Text(uploads.RootElement, "nextPageToken");
            var result = new CatalogPage(videos, next.Length == 0 ? null : next);
            // A disconnect or reconnect must not repopulate account caches from an old request.
            await gate.WaitAsync(ct);
            try
            {
                if (store.Get<Connection>(owner, "youtubeConnection", "current")?.ProtectedToken != connection.ProtectedToken)
                    throw new InvalidOperationException("The YouTube connection changed.");
                if (store.List<UploadSnapshot>(owner, "youtubeUploads").Count >= 128) store.Delete(owner, "youtubeUploads");
                store.Put(owner, "youtubeUploads", key, new UploadSnapshot(DateTimeOffset.UtcNow, result));
            }
            finally { gate.Release(); }
            return result;
        }
        finally { _uploadsGate.Release(); }
    }

    private async Task<JsonDocument> ReadApi(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await clients.CreateClient("JellyTube.Google").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("YouTube uploads are unavailable. Try again or reconnect your account.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public bool Connected(string owner) => store.Get<Connection>(owner, "youtubeConnection", "current") is not null;
    public bool Configured => !string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.YouTubeClientId)
        && !string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.YouTubeClientSecret)
        && Uri.TryCreate(Callback, UriKind.Absolute, out var callback) && callback.Scheme == "https";
    public string Callback => Plugin.Instance.Configuration.PublicBaseUrl.TrimEnd('/') + "/JellyTube/YouTube/Callback";
    private SemaphoreSlim Gate(string owner) => _gates[(int)((uint)StringComparer.Ordinal.GetHashCode(owner) % (uint)_gates.Length)];

    public (string State, string Url) Begin(string owner)
    {
        if (!Configured) throw new InvalidOperationException("An administrator must configure Google OAuth and an HTTPS Jellyfin URL.");
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _pending.Where(entry => entry.Value.Expires <= now || entry.Value.Owner == owner))
            _pending.TryRemove(entry);
        if (_pending.Count >= 256) throw new InvalidOperationException("Too many YouTube connections are pending. Try again shortly.");
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var redirect = Callback;
        var clientId = Plugin.Instance.Configuration.YouTubeClientId;
        _pending[state] = new(owner, verifier, redirect, clientId, now.AddMinutes(10));
        return (state, QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = Scope, ["access_type"] = "offline", ["prompt"] = "consent select_account",
            ["state"] = state, ["code_challenge_method"] = "S256",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        }));
    }

    public async Task Complete(string state, string code, CancellationToken ct)
    {
        if (!_pending.TryGetValue(state, out var pending)) throw new UnauthorizedAccessException("This connection has expired.");
        var gate = Gate(pending.Owner);
        await gate.WaitAsync(ct);
        try
        {
            if (!_pending.TryRemove(state, out pending) || pending.Expires <= DateTimeOffset.UtcNow
                || pending.Redirect != Callback || pending.ClientId != Plugin.Instance.Configuration.YouTubeClientId)
                throw new UnauthorizedAccessException("This connection has expired.");
            access.User(pending.Owner);
            var token = await Exchange(new()
            {
                ["code"] = code, ["code_verifier"] = pending.Verifier,
                ["redirect_uri"] = pending.Redirect, ["grant_type"] = "authorization_code"
            }, ct);
            if (string.IsNullOrEmpty(token.Refresh)) throw new ProviderException("Google did not grant offline access. Reconnect YouTube.");
            var channels = await ReadSubscriptions(token.Access, ct);
            store.Delete(pending.Owner, "youtubeUploads");
            _tokens.TryRemove(pending.Owner, out _);
            store.Put(pending.Owner, "youtubeConnection", "current",
                new Connection(pending.ClientId, _protector.Value.Protect(token.Refresh), DateTimeOffset.UtcNow, channels));
        }
        finally { gate.Release(); }
    }

    public async Task<List<ChannelInfo>> Subscriptions(string owner, bool refresh, CancellationToken ct)
    {
        var gate = Gate(owner);
        await gate.WaitAsync(ct);
        try
        {
            var connection = store.Get<Connection>(owner, "youtubeConnection", "current")
                ?? throw new InvalidOperationException("Connect your YouTube account first.");
            if (!refresh && connection.SyncedAt.AddHours(1) > DateTimeOffset.UtcNow) return connection.Channels;
            if (!Configured || connection.ClientId != Plugin.Instance.Configuration.YouTubeClientId)
                throw new InvalidOperationException("YouTube setup changed. Reconnect your account.");
            var token = await Exchange(new()
            {
                ["refresh_token"] = _protector.Value.Unprotect(connection.ProtectedToken), ["grant_type"] = "refresh_token"
            }, ct);
            var channels = await ReadSubscriptions(token.Access, ct);
            store.Put(owner, "youtubeConnection", "current", connection with
            {
                SyncedAt = DateTimeOffset.UtcNow, Channels = channels,
                ProtectedToken = string.IsNullOrEmpty(token.Refresh) ? connection.ProtectedToken : _protector.Value.Protect(token.Refresh)
            });
            return channels;
        }
        finally { gate.Release(); }
    }

    public async Task Disconnect(string owner, CancellationToken ct)
    {
        var gate = Gate(owner);
        await gate.WaitAsync(ct);
        try
        {
            foreach (var entry in _pending.Where(entry => entry.Value.Owner == owner)) _pending.TryRemove(entry);
            var connection = store.Get<Connection>(owner, "youtubeConnection", "current");
            store.Delete(owner, "youtubeConnection");
            store.Delete(owner, "youtubeUploads");
            _tokens.TryRemove(owner, out _);
            if (connection is null) return;
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["token"] = _protector.Value.Unprotect(connection.ProtectedToken) });
                using var response = await clients.CreateClient("JellyTube.Google").PostAsync("https://oauth2.googleapis.com/revoke", content, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or CryptographicException or OperationCanceledException) { }
        }
        finally { gate.Release(); }
    }

    private async Task<Token> Exchange(Dictionary<string, string> parameters, CancellationToken ct)
    {
        var config = Plugin.Instance.Configuration;
        parameters["client_id"] = config.YouTubeClientId;
        parameters["client_secret"] = config.YouTubeClientSecret;
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await clients.CreateClient("JellyTube.Google").PostAsync("https://oauth2.googleapis.com/token", content, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("Google could not authorize YouTube. Check setup or reconnect your account.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = Extractor.Text(json.RootElement, "access_token");
        if (token.Length == 0) throw new ProviderException("Google did not return a YouTube access token.");
        return new(token, Extractor.Text(json.RootElement, "refresh_token"));
    }

    private async Task<List<ChannelInfo>> ReadSubscriptions(string token, CancellationToken ct)
    {
        var client = clients.CreateClient("JellyTube.Google");
        var channels = new List<ChannelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = new HashSet<string>(StringComparer.Ordinal);
        var page = "";
        while (pages.Add(page) && pages.Count <= 100)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/youtube/v3/subscriptions?part=snippet&mine=true&maxResults=50&order=alphabetical"
                + (page.Length > 0 ? "&pageToken=" + Uri.EscapeDataString(page) : ""));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new ProviderException("YouTube could not load your subscriptions. Check API access or reconnect.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                var snippet = item.GetProperty("snippet");
                var id = Extractor.Text(snippet.GetProperty("resourceId"), "channelId");
                if (id.Length == 24 && id.StartsWith("UC", StringComparison.Ordinal) && seen.Add(id))
                    channels.Add(new(id, Extractor.Text(snippet, "title"), ""));
            }
            page = Extractor.Text(json.RootElement, "nextPageToken");
            if (page.Length == 0) return channels;
        }
        throw new ProviderException("YouTube returned too many subscription pages or repeated a page.");
    }
}
