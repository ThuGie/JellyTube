using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyTube;

public sealed class Catalog(Store store, Extractor extractor, IHttpClientFactory http)
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SemaphoreSlim _topicGate = new(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, CatalogPage Page)> _cache = new();
    private sealed record TopicResult(DateTimeOffset CreatedAt, DateTimeOffset? HistoryClearedAt, string Policy, CatalogPage Page);

    public async Task<CatalogPage> RecommendTopics(string owner, CancellationToken ct)
    {
        if (!store.Settings(owner).RecordHistory) return new([]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await _topicGate.WaitAsync(deadline.Token);
        try
        {
            var settings = store.Settings(owner);
            if (!settings.RecordHistory) return new([]);
            var history = store.List<HistoryEntry>(owner, "history");
            if (history.Count == 0) return new([]);

            var cleared = store.Get<DateTimeOffset?>(owner, "historyClear", "current");
            var config = Plugin.Instance.Configuration;
            var policy = string.Join('|', settings.Region, settings.Language, settings.SafeSearch,
                config.MinimumSafeSearch, !string.IsNullOrWhiteSpace(config.ApiKey));
            var now = DateTimeOffset.UtcNow;
            var cached = store.Get<TopicResult>(owner, "topicRecommendations", "current");
            if (cached is null || cached.CreatedAt.AddHours(24) <= now || cached.HistoryClearedAt != cleared || cached.Policy != policy)
            {
                var recent = history.OrderByDescending(entry => entry.WatchedAt).Take(3);
                var tagSets = await Task.WhenAll(recent.Select(async entry =>
                {
                    if (entry.Video.Tags is { } tags) return tags;
                    try { return (await extractor.Details(entry.Video.Id, deadline.Token)).Tags ?? []; }
                    catch (ProviderException) { return []; }
                }));
                var topic = tagSets.SelectMany(tags => tags.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                    .Where(tag => tag.Length is >= 4 and <= 60
                        && !tag.Equals("youtube", StringComparison.OrdinalIgnoreCase)
                        && !tag.Equals("video", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key)
                    .FirstOrDefault();

                CatalogPage page = new([]);
                if (topic is not null)
                {
                    try { page = await Search(owner, topic, null, deadline.Token); }
                    catch (ProviderException) { /* Channel recommendations remain available. */ }
                }
                if (!store.Settings(owner).RecordHistory || store.Get<DateTimeOffset?>(owner, "historyClear", "current") != cleared)
                    return new([]);
                cached = new(now, cleared, policy, page);
                store.Put(owner, "topicRecommendations", "current", cached);
            }
            var watched = history.Select(entry => entry.Video.Id).ToHashSet(StringComparer.Ordinal);
            var items = cached.Page.Items.Where(video => !watched.Contains(video.Id)).Take(8).ToList();
            foreach (var video in items) store.Put(owner, "video", video.Id, video);
            return new(items);
        }
        finally { _topicGate.Release(); }
    }

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
                Extractor.Text(snippet, "channelId"), "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg",
                PublishedAt: DateTimeOffset.TryParse(Extractor.Text(snippet, "publishedAt"), out var published) ? published : null));
        }
        if (items.Count > 0)
        {
            var durationsUrl = "https://www.googleapis.com/youtube/v3/videos?part=contentDetails&id="
                + string.Join(',', items.Select(item => item.Id)) + "&key=" + Uri.EscapeDataString(Plugin.Instance.Configuration.ApiKey);
            try
            {
                using var durations = await http.CreateClient("JellyTube.Catalog").GetAsync(durationsUrl, ct);
                if (durations.IsSuccessStatusCode)
                {
                    using var metadata = JsonDocument.Parse(await durations.Content.ReadAsStringAsync(ct));
                    var lengths = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var item in metadata.RootElement.GetProperty("items").EnumerateArray())
                    {
                        try { lengths[Extractor.Text(item, "id")] = System.Xml.XmlConvert.ToTimeSpan(Extractor.Text(item.GetProperty("contentDetails"), "duration")).TotalSeconds; }
                        catch (FormatException) { }
                    }
                    items = items.Select(item => lengths.TryGetValue(item.Id, out var seconds) ? item with { DurationSeconds = seconds } : item).ToList();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException) { /* Keep search results when duration lookup fails. */ }
        }
        return new(items, Extractor.Text(doc.RootElement, "nextPageToken", "") is { Length: > 0 } token ? token : null);
    }
}
