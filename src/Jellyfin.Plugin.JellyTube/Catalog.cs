using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyTube;

public sealed class Catalog(Store store, Extractor extractor, YouTubeAccount youtube)
{
    private readonly KeyedGate _searchGates = new();
    private readonly SemaphoreSlim _discovery = new(4);
    private readonly KeyedGate _topicGate = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, CatalogPage Page)> _cache = new();
    private sealed record TopicResult(DateTimeOffset CreatedAt, DateTimeOffset? HistoryClearedAt, string Policy, CatalogPage Page);

    public async Task<CatalogPage> RecommendTopics(string owner, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var topicLease = await _topicGate.Enter(owner, deadline.Token);
        try
        {
            var settings = store.Settings(owner);
            var history = settings.RecordHistory ? store.List<HistoryEntry>(owner, "history") : [];
            var seeds = history.OrderByDescending(entry => entry.WatchedAt).Take(4).Select(entry => entry.Video)
                .Concat(store.List<SavedVideo>(owner, "favorite").OrderByDescending(entry => entry.AddedAt).Take(4).Select(entry => entry.Video))
                .DistinctBy(video => video.Id).Take(6).ToArray();
            if (seeds.Length == 0) return new([]);

            var cleared = store.Get<DateTimeOffset?>(owner, "historyClear", "current");
            var config = Plugin.Instance.Configuration;
            var policy = string.Join('|', "multi-topic-v1", settings.RecordHistory, settings.Region, settings.Language, settings.SafeSearch,
                config.MinimumSafeSearch, !string.IsNullOrWhiteSpace(config.ApiKey), string.Join(',', seeds.Select(v => v.Id)));
            var now = DateTimeOffset.UtcNow;
            var cached = store.Get<TopicResult>(owner, "topicRecommendations", "current");
            if (cached is null || cached.CreatedAt.AddHours(8) <= now || cached.HistoryClearedAt != cleared || cached.Policy != policy)
            {
                var tagSets = await Task.WhenAll(seeds.Select(async video =>
                {
                    if (video.Tags is { Length: > 0 } tags) return tags;
                    try { return (youtube.CanCatalog(owner) ? await youtube.CatalogDetails(owner, video.Id, deadline.Token) : await extractor.Details(video.Id, deadline.Token)).Tags ?? []; }
                    catch (ProviderException) { return []; }
                }));
                var topics = tagSets.SelectMany(tags => tags.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                    .Where(tag => tag.Length is >= 4 and <= 60 && !GenericTopic(tag))
                    .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .ThenByDescending(group => group.Key.Contains(' '))
                    .Select(group => group.Key)
                    .Take(3)
                    .ToList();
                if (topics.Count == 0)
                    topics.AddRange(seeds.Select(video => video.Title.Trim()).Where(title => title.Length is >= 4 and <= 150).Distinct().Take(2));

                var results = new List<CatalogPage>();
                foreach (var topic in topics)
                {
                    try { results.Add(await Search(owner, topic, null, deadline.Token)); }
                    catch (ProviderException) { /* Other topics may still be available from cache. */ }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                }
                if (store.Settings(owner).RecordHistory != settings.RecordHistory || store.Get<DateTimeOffset?>(owner, "historyClear", "current") != cleared)
                    return new([]);
                var excluded = seeds.Select(video => video.Id).ToHashSet(StringComparer.Ordinal);
                var ordered = Enumerable.Range(0, 20).SelectMany(index => results.SelectMany(page => page.Items.Skip(index).Take(1)))
                    .Where(video => excluded.Add(video.Id)).ToList();
                var page = new CatalogPage(ordered);
                cached = new(now, cleared, policy, page);
                store.Put(owner, "topicRecommendations", "current", cached);
            }
            var watched = history.Select(entry => entry.Video.Id).ToHashSet(StringComparer.Ordinal);
            var items = cached.Page.Items.Where(video => !watched.Contains(video.Id)).Take(48).ToList();
            return new(store.CacheVideos(owner, items));
        }
        finally { }
    }

    private static bool GenericTopic(string value) =>
        new[] { "youtube", "video", "shorts", "viral", "trending", "subscribe", "official", "4k video" }
            .Contains(value, StringComparer.OrdinalIgnoreCase);

    public async Task<CatalogPage> Explore(string owner, string topic, string? page, CancellationToken ct)
    {
        if (youtube.CanCatalog(owner)) return await youtube.Explore(owner, topic, page, ct);
        // Provider search already selects the topic. An English-only title filter drops relevant
        // recipes, destinations and localized titles, leaving nearly empty pages.
        return await Search(owner, YouTubeAccount.TopicQuery(topic), page, ct, TimeSpan.FromHours(2));
    }

    internal static bool MatchesExploreTopic(string topic, string title)
    {
        var pattern = topic switch
        {
            "music" => @"\b(music|concert|song|album|orchestra|symphony|choir|acoustic|instrumental|live session)\b",
            "technology" => @"\b(tech(nology)?|software|computer|smartphone|robot\w*|chip\w*|linux|programming|gadget\w*|ai)\b",
            "science" => @"\b(science|nature|wildlife|physics|biology|chemistry|planet\w*|ocean\w*|space|astronom\w*)\b",
            "gaming" => @"\b(gaming|gameplay|video game\w*|walkthrough|playthrough|playstation|xbox|nintendo)\b",
            "food" => @"\b(cook\w*|recipe\w*|baking|bake|food|chef|meal\w*|kitchen)\b",
            "travel" => @"\b(travel\w*|tour\w*|destination\w*|visit\w*|vacation|itinerary|road trip)\b",
            "sports" => @"\b(sport\w*|football|soccer|basketball|baseball|tennis|hockey|rugby|cricket|athletics|olympic\w*)\b",
            _ => ""
        };
        const System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        if (topic == "music" && System.Text.RegularExpressions.Regex.IsMatch(title,
            @"\b(build\w*|shed\w*|woodwork\w*|workshop|renovat\w*|construct\w*|diy|makeover)\b", options))
            return false;
        return pattern.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(title, pattern, options);
    }

    public async Task<CatalogPage> Search(string owner, string query, string? page, CancellationToken ct, TimeSpan? cacheDuration = null)
    {
        query = query.Trim(); if (query.Length is < 2 or > 150 || (page?.Length ?? 0) > 500) throw new ValidationException("Enter a search between 2 and 150 characters.");
        if (youtube.CanCatalog(owner)) return await youtube.CatalogVideos(owner, "search", query, page, ct);
        var settings = store.Settings(owner); var config = Plugin.Instance.Configuration;
        var key = string.Join('|', query, page, settings.Region, settings.Language, settings.SafeSearch, config.MinimumSafeSearch, string.IsNullOrEmpty(config.ApiKey) ? "extractor" : "official");

        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Page;
        using var lease = await _searchGates.Enter(key, ct);
        await _discovery.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Page;
            if (!store.ClaimSearch(owner, config.SearchCallsPerDay, config.UserSearchCallsPerDay, DateTimeOffset.UtcNow))
                throw new ProviderException("Today's search budget has been reached. Saved videos and native playback remain available.");
            var result = await extractor.Search(query, ct, page);
            if (_cache.Count > 200) foreach (var old in _cache.OrderBy(x => x.Value.Expires).Take(50)) _cache.TryRemove(old.Key, out _);
            _cache[key] = (DateTimeOffset.UtcNow + (cacheDuration ?? TimeSpan.FromMinutes(10)), result); return result;
        }
        finally { _discovery.Release(); }
    }
}
