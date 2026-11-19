using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.JellyTube;

public sealed record FeedSource(string Kind, string Id, string? Token = null);
public sealed record FeedSnapshot(DateTimeOffset CreatedAt, string Version, CatalogPage Page);
public sealed record FeedCursor(DateTimeOffset CreatedAt, string Policy, List<FeedSource> Sources, List<VideoInfo> Buffer, string[] Seen);
public sealed record FeedPage(List<VideoInfo> Items, string? NextPageToken, string SnapshotVersion, DateTimeOffset UpdatedAt, bool Stale, bool Refreshing);

/// <summary>Owner-scoped persistent feed snapshots; background refresh never reorders an open page.</summary>
public sealed class Feeds(Store store, YouTubeAccount youtube, Catalog catalog, Extractor extractor, IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<FeedSnapshot>>> _requests = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failures = new();
    private readonly SemaphoreSlim _capacity = new(4);
    public async Task<FeedPage> Get(string owner, string kind, string topic, string? page, CancellationToken ct)
    {
        if (kind is not ("for-you" or "following" or "general" or "explore")) throw new ArgumentException("Unknown feed.");
        if (topic is not ("general" or "music" or "gaming" or "sports" or "travel" or "technology" or "science" or "food")) throw new ArgumentException("Unknown topic.");
        if (page is not null && (!Guid.TryParseExact(page, "N", out _) || page.Length != 32)) throw new ArgumentException("Invalid continuation.");
        var policy = Policy(owner);
        var key = Hash(policy + "|" + kind + "|" + topic + "|" + page);
        var cached = store.Get<FeedSnapshot>(owner, "feed", key);
        var age = DateTimeOffset.UtcNow - (cached?.CreatedAt ?? DateTimeOffset.MinValue);
        var fresh = age < (kind == "explore" ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(15));
        if (cached is not null && fresh) { Performance.Record("feed.hit", 0); return Response(cached, false, false); }
        var requestKey = owner + ":" + key;
        if (_failures.TryGetValue(requestKey, out var retry) && retry > DateTimeOffset.UtcNow)
        {
            if (cached is not null && age < TimeSpan.FromHours(24)) return Response(cached, true, false);
            throw new ProviderException("The feed provider is temporarily unavailable. Try again shortly.");
        }
        var pending = _requests.GetOrAdd(requestKey, _ => new(() => Refresh(owner, kind, topic, page, policy, key, requestKey)));
        var task = pending.Value;
        _ = task.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (cached is not null && age < TimeSpan.FromHours(24)) return Response(cached, true, true);
        return Response(await task.WaitAsync(ct), false, false);
    }
    private FeedPage Response(FeedSnapshot value, bool stale, bool refreshing) =>
        new(store.EnrichVideos(value.Page.Items), value.Page.NextPageToken, value.Version, value.CreatedAt, stale, refreshing);
    private string Policy(string owner) => Hash("topics-v2|" + JsonSerializer.Serialize(store.Settings(owner), Store.Json) + "|" +
        youtube.ConnectionVersion(owner) + "|" + Plugin.Instance.Configuration.MinimumSafeSearch + "|" + Plugin.Instance.Configuration.ApiKey + "|" +
        string.Join(',', store.List<SavedChannel>(owner, "channel").Select(c => c.Channel.Id)) + "|" +
        store.Get<DateTimeOffset?>(owner, "historyClear", "current"));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private async Task<FeedSnapshot> Refresh(string owner, string kind, string topic, string? page, string policy, string key, string requestKey)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        try
        {
            using var timing = Performance.Measure("feed.refresh");
            using (var wait = Performance.Measure("feed.queue")) await _capacity.WaitAsync(ct);
            try
            {
                FeedCursor state;
                if (page is not null)
                {
                    state = store.Get<FeedCursor>(owner, "feedCursor", page) ?? throw new ArgumentException("This feed page expired. Refresh the feed.");
                    if (state.Policy != policy || state.CreatedAt.AddHours(24) < DateTimeOffset.UtcNow) throw new ArgumentException("This feed page expired. Refresh the feed.");
                }
                else
                {
                    var sources = new List<FeedSource>();
                    if (kind == "explore" && topic != "general") sources.Add(new("explore", topic));
                    else if (youtube.Connected(owner) && kind is "for-you" or "following")
                        sources.AddRange((await youtube.Subscriptions(owner, false, ct)).Select(c => new FeedSource("connected", c.Id)));
                    else
                    {
                        if (kind == "for-you") sources.Add(new("topics", ""));
                        if (kind != "following") sources.AddRange(new[] { "technology", "music", "gaming", "science", "food", "travel", "sports" }.Select(t => new FeedSource("explore", t)));
                        var followed = store.List<SavedChannel>(owner, "channel").Select(c => new FeedSource("channel", c.Channel.Id)).ToList();
                        if (kind == "following") sources.AddRange(followed);
                        else if (kind == "for-you")
                            for (var i = 0; i < followed.Count; i++) sources.Insert(Math.Min(1 + i * 3, sources.Count), followed[i]);
                    }
                    state = new(DateTimeOffset.UtcNow, policy, sources, [], []);
                }
                var sourcesLeft = state.Sources.ToList();
                var buffer = state.Buffer.ToList();
                var seen = state.Seen.ToHashSet(StringComparer.Ordinal);
                // Fetch at most four upstream pages per request. Cursor retains untouched channels and leftovers.
                if (buffer.Count < 24 && sourcesLeft.Count > 0)
                {
                    var batch = sourcesLeft.Take(4).ToArray(); sourcesLeft.RemoveRange(0, batch.Length);
                    var tasks = batch.Select(async source =>
                    {
                        try { return (Source: source, Page: await Read(owner, source, ct), Error: (Exception?)null); }
                        catch (Exception ex) when (ex is ProviderException or HttpRequestException or OperationCanceledException)
                        { return (Source: source, Page: new CatalogPage([]), Error: ex); }
                    }).ToArray();
                    var outstanding = tasks.ToList();
                    while (outstanding.Count > 0)
                    {
                        var done = await Task.WhenAny(outstanding).WaitAsync(ct);
                        outstanding.Remove(done);
                        if ((await done).Page.Items.Count > 0) break;
                    }
                    if (outstanding.Count > 0) await Task.WhenAny(Task.WhenAll(outstanding), Task.Delay(200, ct));
                    var results = tasks.Where(task => task.IsCompletedSuccessfully).Select(task => task.Result).ToArray();
                    sourcesLeft.InsertRange(0, batch.Where((_, index) => !tasks[index].IsCompletedSuccessfully));
                    if (buffer.Count == 0 && results.All(r => r.Error is not null)) throw new ProviderException("This feed is temporarily unavailable. Try again shortly.");
                    foreach (var result in results)
                    {
                        if (result.Error is not null) sourcesLeft.Add(result.Source);
                        else if (result.Page.NextPageToken is { } next && next != result.Source.Token) sourcesLeft.Add(result.Source with { Token = next });
                    }
                    for (var i = 0; i < results.Max(r => r.Page.Items.Count); i++)
                        foreach (var result in results)
                            if (i < result.Page.Items.Count && seen.Add(result.Page.Items[i].Id)) buffer.Add(result.Page.Items[i]);
                }
                var items = buffer.Take(24).ToList(); buffer = buffer.Skip(24).ToList();
                string? nextCursor = null;
                if (buffer.Count > 0 || sourcesLeft.Count > 0)
                {
                    nextCursor = Guid.NewGuid().ToString("N");
                    if (Policy(owner) != policy) throw new InvalidOperationException("Your feed settings changed. Refresh the feed.");
                    store.Put(owner, "feedCursor", nextCursor, new FeedCursor(DateTimeOffset.UtcNow, policy, sourcesLeft, buffer, seen.TakeLast(10000).ToArray()));
                    store.Trim(owner, "feedCursor", 128);
                }
                if (Policy(owner) != policy) throw new InvalidOperationException("Your feed settings changed. Refresh the feed.");
                items = store.CacheVideos(owner, items);
                var snapshot = new FeedSnapshot(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), new(items, nextCursor));
                store.Put(owner, "feed", key, snapshot); store.Trim(owner, "feed", 128);
                return snapshot;
            }
            finally { _capacity.Release(); }
        }
        catch
        {
            if (_failures.Count >= 512) foreach (var old in _failures.OrderBy(pair => pair.Value).Take(128)) _failures.TryRemove(old.Key, out _);
            _failures[requestKey] = DateTimeOffset.UtcNow.AddSeconds(30);
            throw;
        }
        finally { _requests.TryRemove(requestKey, out _); }
    }
    private Task<CatalogPage> Read(string owner, FeedSource source, CancellationToken ct) => source.Kind switch
    {
        "connected" => youtube.Uploads(owner, source.Id, source.Token, ct, waitForRefresh: true),
        "channel" => youtube.CanCatalog(owner) ? youtube.CatalogVideos(owner, "channel", source.Id, source.Token, ct) : extractor.Uploads(source.Id, ct, source.Token),
        "topics" => catalog.RecommendTopics(owner, ct),
        _ => youtube.CanCatalog(owner) ? youtube.Explore(owner, source.Id, source.Token, ct, waitForRefresh: true) : catalog.Explore(owner, source.Id, source.Token, ct)
    };
}
