using System.Reflection;
using System.Text;
using Jellyfin.Plugin.JellyTube;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JellyTube.Tests;

public sealed class OfficialCatalogTests : IDisposable
{
    private const string Video = "aqz-KE-bpKQ";
    private static readonly string Channel = "UC" + new string('a', 22);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellytube-official-" + Guid.NewGuid().ToString("N"));
    private readonly Store _store;
    private readonly GoogleFactory _http = new();
    private readonly YouTubeAccount _youtube;
    public OfficialCatalogTests()
    {
        SQLitePCL.Batteries_V2.Init(); Directory.CreateDirectory(_folder);
        var paths = DispatchProxy.Create<IApplicationPaths, Defaults>(); ((Defaults)(object)paths).Directory = _folder;
        var xml = DispatchProxy.Create<IXmlSerializer, Defaults>();
        var plugin = new Plugin(paths, xml); plugin.UpdateConfiguration(new Configuration { ApiKey = "test-key" });
        _store = new(Path.Combine(_folder, "test.db"));
        _youtube = new(_store, _http, new Access(null!, null!), new Lifetime());
    }
    [Fact]
    public async Task ChannelUsesOfficialUploadsAndReturnsCompletePublicCards()
    {
        var page = await _youtube.CatalogVideos("alice", "channel", Channel, null, CancellationToken.None);
        var video = Assert.Single(page.Items);
        Assert.Equal("Full description", video.Description); Assert.NotNull(video.PublishedAt);
        Assert.Equal(600, video.DurationSeconds); Assert.Equal("next-page", page.NextPageToken);
        Assert.Contains(_http.Paths, path => path == "/youtube/v3/channels");
        Assert.Contains(_http.Paths, path => path == "/youtube/v3/playlistItems");
        var calls = _http.Paths.Count;
        await _youtube.CatalogVideos("alice", "channel", Channel, null, CancellationToken.None);
        Assert.Equal(calls, _http.Paths.Count);
        await _youtube.CatalogVideos("alice", "channel", Channel, page.NextPageToken, CancellationToken.None);
        Assert.Equal(calls + 2, _http.Paths.Count); // playlist mapping is reused; IDs and metadata remain batched.
        Assert.Equal(0, _store.Count("bob", "youtubeCatalog"));
    }
    [Fact]
    public async Task SearchUsesOneMetadataBatchAndDetailsReuseItAcrossAccounts()
    {
        Assert.True(_youtube.CanCatalog("alice"));
        var page = await _youtube.CatalogVideos("alice", "search", "Blender", null, CancellationToken.None);
        Assert.Single(page.Items); Assert.Equal(2, _http.Paths.Count);
        _store.CacheVideos("alice", page.Items);
        var details = await _youtube.CatalogDetails("bob", Video, CancellationToken.None);
        Assert.Equal("Full description", details.Description); Assert.Equal(2, _http.Paths.Count);
        Assert.Null(_store.CachedVideo("bob", Video)); // reading public details does not import another user's discovery history.
    }
    [Fact]
    public async Task PublicPlaylistPreservesContinuationAndPrivatePlaylistsAreRejected()
    {
        var page = await _youtube.CatalogVideos("alice", "playlist", "PLpublic123456", null, CancellationToken.None);
        Assert.Single(page.Items); Assert.Equal("next-page", page.NextPageToken);
        await Assert.ThrowsAsync<ProviderException>(() => _youtube.CatalogVideos("alice", "playlist", "PLprivate123456", null, CancellationToken.None));
    }
    [Fact]
    public async Task ApiKeyExploreUsesCategoryChartWithoutSpendingSearchQuota()
    {
        var page = await _youtube.Explore("alice", "music", null, CancellationToken.None);
        Assert.Single(page.Items); Assert.DoesNotContain("/youtube/v3/search", _http.Paths);
        Assert.Single(_http.Paths);
        await _youtube.Explore("alice", "music", null, CancellationToken.None);
        Assert.Single(_http.Paths);
    }
    [Fact]
    public async Task EveryTopicLoadsAndSemanticTabsHaveIndependentCompletePages()
    {
        var pages = new Dictionary<string, CatalogPage>();
        foreach (var topic in new[] { "music", "gaming", "sports", "travel", "technology", "science", "food" })
            pages[topic] = await _youtube.Explore("alice", topic, null, CancellationToken.None);
        Assert.All(pages.Values, page => Assert.NotEmpty(page.Items));
        Assert.Empty(pages["technology"].Items.Select(v => v.Id).Intersect(pages["science"].Items.Select(v => v.Id)));
        Assert.Equal(30, pages["travel"].Items.Count); Assert.Equal(30, pages["food"].Items.Count);
        Assert.All(pages["food"].Items, video => { Assert.NotNull(video.PublishedAt); Assert.NotNull(video.Description); });
        Assert.Equal("next-page", pages["food"].NextPageToken);
        Assert.DoesNotContain(_http.Queries, query => query.Contains("videoCategoryId=19") || query.Contains("videoCategoryId=28") || query.Contains("videoCategoryId=26"));
        var requests = _http.Paths.Count;
        await _youtube.Explore("bob", "food", null, CancellationToken.None);
        Assert.Equal(requests, _http.Paths.Count);
        Assert.True(_store.ClaimSearch("alice", 90, 1, DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task MissingChartFallsBackToPaginatedTopicSearch()
    {
        _http.MissingCharts = true;
        var page = await _youtube.Explore("alice", "music", null, CancellationToken.None);
        Assert.Equal(30, page.Items.Count); Assert.Equal("next-page", page.NextPageToken);
        await _youtube.Explore("alice", "music", page.NextPageToken, CancellationToken.None);
        Assert.Contains(_http.Queries, query => query.Contains("pageToken=next-page"));
    }
    public class Defaults : DispatchProxy
    {
        public string Directory = "";
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(void) ? null : targetMethod?.ReturnType == typeof(string) ? Directory
                : targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class GoogleFactory : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<string> Paths = [];
        public readonly List<string> Queries = [];
        public bool MissingCharts;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("test-key", query["key"].ToString());
            Paths.Add(request.RequestUri.AbsolutePath); Queries.Add(request.RequestUri.Query);
            if (MissingCharts && query.ContainsKey("chart")) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            var topic = new[] { "travel", "technology", "science", "food", "music", "gaming", "sports" }
                .FirstOrDefault(value => query.TryGetValue("q", out var search) && search.ToString() == YouTubeAccount.TopicQuery(value));
            if (request.RequestUri.AbsolutePath.EndsWith("/search") && topic is not null)
            {
                var ids = Enumerable.Range(0, 30).Select(index => topic[..4] + index.ToString("D7"));
                var body = System.Text.Json.JsonSerializer.Serialize(new { nextPageToken = "next-page", items = ids.Select(id => new { id = new { videoId = id } }) });
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            var metadataIds = query.TryGetValue("id", out var requestedIds) ? requestedIds.ToString().Split(',') : [""];
            if (request.RequestUri.AbsolutePath.EndsWith("/videos") && metadataIds[0].Length == 11 && metadataIds[0] != "aqz-KE-bpKQ" && !query.ContainsKey("chart"))
            {
                var body = System.Text.Json.JsonSerializer.Serialize(new { items = metadataIds.Select(id => new
                {
                    id, snippet = new { title = "Topic video", channelTitle = "Creator", channelId = Channel, description = "Complete topic description", publishedAt = "2026-01-01T00:00:00Z", tags = new[] { "topic" } },
                    contentDetails = new { duration = "PT10M" }, status = new { privacyStatus = "public" }
                }) });
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            var json = request.RequestUri.AbsolutePath.Split('/').Last() switch
            {
                "channels" => """{"items":[{"snippet":{"title":"Creator"},"contentDetails":{"relatedPlaylists":{"uploads":"UUuploads"}}}]}""",
                "playlists" => query["id"].ToString().Contains("private")
                    ? """{"items":[{"status":{"privacyStatus":"private"}}]}""" : """{"items":[{"status":{"privacyStatus":"public"}}]}""",
                "playlistItems" => """{"nextPageToken":"next-page","items":[{"contentDetails":{"videoId":"aqz-KE-bpKQ"}},{"contentDetails":{"videoId":"V12x80R_t_Y"}}]}""",
                "search" => """{"items":[{"id":{"videoId":"aqz-KE-bpKQ"}},{"id":{"videoId":"V12x80R_t_Y"}}]}""",
                "videos" => """{"items":[{"id":"aqz-KE-bpKQ","snippet":{"title":"Public video","description":"Full description","channelTitle":"Creator","channelId":"UCaaaaaaaaaaaaaaaaaaaaaa","publishedAt":"2026-01-01T00:00:00Z","tags":["tag"]},"contentDetails":{"duration":"PT10M"},"status":{"privacyStatus":"public"}},{"id":"V12x80R_t_Y","status":{"privacyStatus":"private"}}]}""",
                _ => throw new InvalidOperationException("Unexpected official API route")
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
