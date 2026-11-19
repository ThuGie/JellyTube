using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyTube;

public sealed partial class YouTubeAccount
{
    private readonly KeyedGate _catalogGates = new();
    private readonly SemaphoreSlim _catalogSlots = new(4);
    private sealed record DiscoverySnapshot(DateTimeOffset CreatedAt, List<DiscoveryResult> Items);
    public bool CanCatalog(string owner) => Connected(owner) || !string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.ApiKey);
    private string CatalogKey(string owner, string kind, string value, string? page) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', kind, value, page, JsonSerializer.Serialize(store.Settings(owner), Store.Json), ConnectionVersion(owner),
            Plugin.Instance.Configuration.ApiKey, Plugin.Instance.Configuration.MinimumSafeSearch))));
    private void ClaimCatalogSearch(string owner)
    {
        var config = Plugin.Instance.Configuration;
        if (!store.ClaimSearch(owner, config.SearchCallsPerDay, config.UserSearchCallsPerDay, DateTimeOffset.UtcNow))
            throw new ProviderException("Today's search budget has been reached. Saved videos and playback remain available.");
    }
    private async Task<JsonDocument> CatalogApi(string owner, string path, CancellationToken ct)
    {
        if (Connected(owner))
        {
            var (_, token) = await AccessToken(owner, ct);
            return await ReadApi(path, token.Access, ct);
        }
        var key = Plugin.Instance.Configuration.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("YouTube API access is not configured.");
        using var timing = Performance.Measure("google.request");
        using var response = await clients.CreateClient("JellyTube.Google").GetAsync(
            "https://www.googleapis.com/youtube/v3/" + path + "&key=" + Uri.EscapeDataString(key), ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("YouTube data is unavailable. Check API access or try again shortly.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private async Task<List<VideoInfo>> CatalogMetadata(string owner, IEnumerable<string> ids, CancellationToken ct)
    {
        var result = new List<VideoInfo>();
        foreach (var batch in ids.Distinct().Chunk(50))
        {
            using var json = await CatalogApi(owner, "videos?part=snippet,contentDetails,status&fields=items(id,snippet(title,channelTitle,channelId,description,publishedAt,tags),contentDetails/duration,status/privacyStatus)&id=" + string.Join(',', batch.Select(Rules.VideoId)), ct);
            result.AddRange(json.RootElement.GetProperty("items").EnumerateArray().Where(IsPublic).Select(ParseVideo));
        }
        return result;
    }
    private sealed record ChannelSnapshot(DateTimeOffset CreatedAt, ChannelInfo Channel);
    public async Task<ChannelInfo> CatalogChannel(string owner, string id, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^UC[A-Za-z0-9_-]{22}$")) throw new ArgumentException("Invalid channel.");
        using var lease = await _catalogGates.Enter("channel:" + id, ct);
        var cached = store.Get<ChannelSnapshot>("public", "channelMetadata", id);
        if (cached is not null && cached.CreatedAt.AddHours(6) > DateTimeOffset.UtcNow) return cached.Channel;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await _catalogSlots.WaitAsync(timeout.Token);
        try
        {
            using var json = await CatalogApi(owner, "channels?part=snippet&fields=items(snippet/title)&id=" + Uri.EscapeDataString(id), timeout.Token);
            var item = json.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault();
            if (item.ValueKind == JsonValueKind.Undefined) throw new ProviderException("This channel is unavailable.");
            var result = new ChannelInfo(id, Extractor.Text(item.GetProperty("snippet"), "title"), "");
            store.Put("public", "channelMetadata", id, new ChannelSnapshot(DateTimeOffset.UtcNow, result)); store.Trim("public", "channelMetadata", 1000);
            return result;
        }
        finally { _catalogSlots.Release(); }
    }
    public async Task<VideoInfo> CatalogDetails(string owner, string id, CancellationToken ct)
    {
        Rules.VideoId(id);
        using var lease = await _catalogGates.Enter("details:" + id, ct);
        var cached = store.Get<Store.PublicVideo>("public", "metadata", id);
        if (cached is { Video.Tags: not null } && cached.CreatedAt.AddHours(6) > DateTimeOffset.UtcNow) return cached.Video;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await _catalogSlots.WaitAsync(timeout.Token);
        try
        {
            using var timing = Performance.Measure("metadata.google");
            var video = (await CatalogMetadata(owner, [id], timeout.Token)).FirstOrDefault() ?? throw new ProviderException("This public video is unavailable.");
            return store.CacheVideos(owner, [video])[0];
        }
        finally { _catalogSlots.Release(); }
    }
    public async Task<CatalogPage> CatalogVideos(string owner, string kind, string value, string? page, CancellationToken ct)
    {
        if ((page?.Length ?? 0) > 512) throw new ArgumentException("Invalid continuation.");
        if (kind == "search") { value = value.Trim(); if (value.Length is < 2 or > 150) throw new ArgumentException("Enter a search between 2 and 150 characters."); }
        else if (kind == "channel") { if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^UC[A-Za-z0-9_-]{22}$")) throw new ArgumentException("Invalid channel."); }
        else if (kind != "playlist" || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]{10,100}$")) throw new ArgumentException("Invalid playlist.");
        var key = CatalogKey(owner, kind, value, page);
        var cached = store.Get<UploadSnapshot>(owner, "youtubeCatalog", key);
        var ttl = kind == "search" ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(15);
        if (cached is not null && DateTimeOffset.UtcNow - cached.CreatedAt < ttl) return cached.Page;
        using var lease = await _catalogGates.Enter(owner + ":" + key, ct);
        cached = store.Get<UploadSnapshot>(owner, "youtubeCatalog", key);
        if (cached is not null && DateTimeOffset.UtcNow - cached.CreatedAt < ttl) return cached.Page;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await _catalogSlots.WaitAsync(timeout.Token);
        try
        {
            var settings = store.Settings(owner);
            string path;
            if (kind == "search")
            {
                ClaimCatalogSearch(owner);
                var safe = Plugin.Instance.Configuration.MinimumSafeSearch == "strict" ? "strict" : settings.SafeSearch;
                path = "search?part=snippet&fields=nextPageToken,items(id/videoId)&type=video&maxResults=20&q=" + Uri.EscapeDataString(value)
                    + "&regionCode=" + Uri.EscapeDataString(settings.Region) + "&relevanceLanguage=" + Uri.EscapeDataString(settings.Language) + "&safeSearch=" + safe;
            }
            else
            {
                var playlist = value;
                if (kind == "channel")
                {
                    playlist = store.Get<string>("public", "channelUploads", value) ?? "";
                    if (playlist.Length == 0)
                    {
                        using var channels = await CatalogApi(owner, "channels?part=contentDetails&fields=items(contentDetails/relatedPlaylists/uploads)&id=" + Uri.EscapeDataString(value), timeout.Token);
                        var item = channels.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault();
                        if (item.ValueKind == JsonValueKind.Undefined) return new([]);
                        playlist = Extractor.Text(item.GetProperty("contentDetails").GetProperty("relatedPlaylists"), "uploads");
                        store.Put("public", "channelUploads", value, playlist); store.Trim("public", "channelUploads", 1000);
                    }
                }
                else
                {
                    using var playlists = await CatalogApi(owner, "playlists?part=status&fields=items(status/privacyStatus)&id=" + Uri.EscapeDataString(value), timeout.Token);
                    var item = playlists.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault();
                    if (item.ValueKind == JsonValueKind.Undefined || Extractor.Text(item.GetProperty("status"), "privacyStatus") != "public")
                        throw new ProviderException("Only public playlists are supported.");
                }
                path = "playlistItems?part=contentDetails&fields=nextPageToken,items(contentDetails/videoId)&maxResults=20&playlistId=" + Uri.EscapeDataString(playlist);
            }
            if (page is not null) path += "&pageToken=" + Uri.EscapeDataString(page);
            using var response = await CatalogApi(owner, path, timeout.Token);
            var ids = response.RootElement.GetProperty("items").EnumerateArray().Select(item => kind == "search"
                ? Extractor.Text(item.GetProperty("id"), "videoId") : Extractor.Text(item.GetProperty("contentDetails"), "videoId")).Where(id => id.Length == 11).Distinct().ToArray();
            var videos = await CatalogMetadata(owner, ids, timeout.Token);
            var byId = videos.ToDictionary(video => video.Id);
            var next = Extractor.Text(response.RootElement, "nextPageToken");
            var result = new CatalogPage(ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), next.Length == 0 ? null : next);
            // Reconnect/disconnect or policy changes cannot repopulate an old account snapshot.
            if (CatalogKey(owner, kind, value, page) != key) throw new InvalidOperationException("Your YouTube settings changed. Retry the page.");
            store.Put(owner, "youtubeCatalog", key, new UploadSnapshot(DateTimeOffset.UtcNow, result)); store.Trim(owner, "youtubeCatalog", 128);
            return result;
        }
        catch (Exception error) when (cached is not null && cached.CreatedAt.AddHours(24) > DateTimeOffset.UtcNow
            && error is ProviderException or HttpRequestException or OperationCanceledException)
        { return cached.Page; }
        finally { _catalogSlots.Release(); }
    }
    public async Task<List<DiscoveryResult>> CatalogDiscover(string owner, string kind, string query, CancellationToken ct)
    {
        if (kind is not ("channels" or "playlists")) throw new ArgumentException("Invalid discovery type.");
        query = query.Trim(); if (query.Length is < 2 or > 150) throw new ArgumentException("Enter a search between 2 and 150 characters.");
        var key = CatalogKey(owner, kind, query, null);
        using var lease = await _catalogGates.Enter(owner + ":" + key, ct);
        var cached = store.Get<DiscoverySnapshot>(owner, "youtubeDiscover", key);
        if (cached is not null && cached.CreatedAt.AddMinutes(10) > DateTimeOffset.UtcNow) return cached.Items;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await _catalogSlots.WaitAsync(timeout.Token);
        try
        {
            ClaimCatalogSearch(owner);
            var settings = store.Settings(owner);
            var safe = Plugin.Instance.Configuration.MinimumSafeSearch == "strict" ? "strict" : settings.SafeSearch;
            using var json = await CatalogApi(owner, "search?part=snippet&fields=items(id,snippet(title,channelTitle))&maxResults=20&type="
                + (kind == "channels" ? "channel" : "playlist") + "&q=" + Uri.EscapeDataString(query)
                + "&regionCode=" + Uri.EscapeDataString(settings.Region) + "&relevanceLanguage=" + Uri.EscapeDataString(settings.Language) + "&safeSearch=" + safe, timeout.Token);
            var items = json.RootElement.GetProperty("items").EnumerateArray().Select(item => new DiscoveryResult(kind,
                Extractor.Text(item.GetProperty("id"), kind == "channels" ? "channelId" : "playlistId"),
                System.Net.WebUtility.HtmlDecode(Extractor.Text(item.GetProperty("snippet"), "title")), Extractor.Text(item.GetProperty("snippet"), "channelTitle"))).ToList();
            if (CatalogKey(owner, kind, query, null) != key) throw new InvalidOperationException("Your YouTube settings changed. Retry the page.");
            store.Put(owner, "youtubeDiscover", key, new DiscoverySnapshot(DateTimeOffset.UtcNow, items)); store.Trim(owner, "youtubeDiscover", 128);
            return items;
        }
        finally { _catalogSlots.Release(); }
    }
}
