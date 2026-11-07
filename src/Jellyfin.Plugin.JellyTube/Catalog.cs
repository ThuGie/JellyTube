using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyTube;

public sealed class Catalog(Store store, Extractor extractor, IHttpClientFactory http)
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, CatalogPage Page)> _cache = new();
    public async Task<CatalogPage> Search(string owner, string query, string? page, CancellationToken ct)
    {
        query = query.Trim(); if (query.Length is < 2 or > 150 || (page?.Length ?? 0) > 500) throw new ValidationException("Enter a search between 2 and 150 characters.");
        var settings = store.Settings(owner); var config = Plugin.Instance.Configuration;
        var key = string.Join('|', query, page, settings.Region, settings.Language, settings.SafeSearch, config.MinimumSafeSearch, string.IsNullOrEmpty(config.ApiKey) ? "extractor" : "official");
        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Page;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Page;
            if (!store.ClaimSearch(owner, config.SearchCallsPerDay, config.UserSearchCallsPerDay, DateTimeOffset.UtcNow))
                throw new ProviderException("Today's search budget has been reached. Saved videos and native playback remain available.");
            CatalogPage result;
            if (!string.IsNullOrWhiteSpace(config.ApiKey)) result = await OfficialSearch(query, page, settings, ct);
            else
            {
                if (!string.IsNullOrEmpty(page)) throw new ValidationException("Extractor search returns a bounded first page. Refine your query.");
                result = await extractor.Search(query, ct);
            }
            if (_cache.Count > 200) foreach (var old in _cache.OrderBy(x => x.Value.Expires).Take(50)) _cache.TryRemove(old.Key, out _);
            _cache[key] = (DateTimeOffset.UtcNow.AddMinutes(10), result); return result;
        }
        finally { _gate.Release(); }
    }
    private async Task<CatalogPage> OfficialSearch(string query, string? page, UserSettings settings, CancellationToken ct)
    {
        var url = "https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&maxResults=20&q=" + Uri.EscapeDataString(query)
            + "&regionCode=" + settings.Region + "&relevanceLanguage=" + Uri.EscapeDataString(settings.Language)
            + "&safeSearch=" + (Plugin.Instance.Configuration.MinimumSafeSearch == "strict" ? "strict" : settings.SafeSearch) + "&key=" + Uri.EscapeDataString(Plugin.Instance.Configuration.ApiKey)
            + (string.IsNullOrEmpty(page) ? "" : "&pageToken=" + Uri.EscapeDataString(page));
        using var response = await http.CreateClient("JellyTube.Catalog").GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("YouTube search is unavailable. Check the API key and project quota in administrator settings.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var items = new List<VideoInfo>();
        foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var id = e.GetProperty("id").GetProperty("videoId").GetString()!; Rules.VideoId(id);
            var snippet = e.GetProperty("snippet");
            items.Add(new(id, System.Net.WebUtility.HtmlDecode(Extractor.Text(snippet, "title")), Extractor.Text(snippet, "channelTitle"),
                Extractor.Text(snippet, "channelId"), "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg", Description: Extractor.Text(snippet, "description")));
        }
        return new(items, Extractor.Text(doc.RootElement, "nextPageToken", "") is { Length: > 0 } token ? token : null);
    }
}
