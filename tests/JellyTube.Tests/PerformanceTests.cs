using Jellyfin.Plugin.JellyTube;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JellyTube.Tests;

public sealed class PerformanceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellytube-perf-" + Guid.NewGuid().ToString("N"));
    private Store Open() { SQLitePCL.Batteries_V2.Init(); return new(Path.Combine(_folder, "test.db")); }
    [Fact]
    public void MetadataIsSharedButDiscoveryReferencesArePrivate()
    {
        var store = Open();
        var video = new VideoInfo("aqz-KE-bpKQ", "Title", "Creator", "channel", "thumbnail", 600, "Full description", DateTimeOffset.UtcNow, ["tag"]);
        store.CacheVideos("alice", [video]);
        Assert.Null(store.CachedVideo("bob", video.Id));
        var response = store.CacheVideos("bob", [video with { Description = null, Tags = null }]);
        Assert.Equal("Full description", Assert.Single(response).Description);
        Assert.Equal("Full description", Assert.Single(store.EnrichVideos([video with { Description = null, Tags = null }])).Description);
        Assert.Equal("Full description", store.CachedVideo("bob", video.Id)?.Description);
        Assert.Equal(1, store.Count("public", "metadata"));
        var created = store.Get<Store.PublicVideo>("public", "metadata", video.Id)!.CreatedAt;
        store.CacheVideos("alice", [video]);
        Assert.Equal(created, store.Get<Store.PublicVideo>("public", "metadata", video.Id)!.CreatedAt);
        store.Delete("alice", "videoRef");
        Assert.Null(store.CachedVideo("alice", video.Id));
        Assert.NotNull(store.CachedVideo("bob", video.Id));
    }
    [Fact]
    public async Task BatchedWritesAndIndependentReadsKeepAccountStateIntact()
    {
        var store = Open();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(owner => Task.Run(() =>
        {
            store.PutMany(owner.ToString(), "test", Enumerable.Range(0, 50).Select(i => (i.ToString("D3"), i)));
            Assert.Equal(50, store.Count(owner.ToString(), "test"));
            Assert.Equal(5, store.List<int>(owner.ToString(), "test", 5).Count);
        })));
        store.Trim("0", "test", 10);
        Assert.Equal(10, store.Count("0", "test"));
        Assert.Equal(50, store.Count("1", "test"));
        Assert.Equal(10, Open().Count("0", "test"));
    }
    [Fact]
    public void VersionOneDatabaseMigratesWithoutLosingPrivateRecords()
    {
        SQLitePCL.Batteries_V2.Init(); Directory.CreateDirectory(_folder);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(_folder, "test.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE records(owner TEXT,kind TEXT,id TEXT,json TEXT,PRIMARY KEY(owner,kind,id)); INSERT INTO records VALUES('alice','favorite','saved','123'); PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
        }
        var store = Open();
        Assert.Equal(123, store.Get<int>("alice", "favorite", "saved"));
        store.Put("alice", "favorite", "other", 456);
        Assert.Equal(2, store.Count("alice", "favorite"));
    }
    [Fact]
    public async Task ExactKeyGateDoesNotBlockUnrelatedWorkOrLeakCancelledWaiters()
    {
        var gates = new KeyedGate();
        using (await gates.Enter("first", CancellationToken.None))
        {
            using var independent = await gates.Enter("second", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gates.Enter("first", cancelled.Token));
        }
        using var reacquired = await gates.Enter("first", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }
    [Fact]
    public async Task ManagedPaginationReusesContinuationAndReplaysCachedPages()
    {
        await using var pages = new EnumerationPages();
        var enumerated = 0;
        async IAsyncEnumerable<VideoInfo> Videos()
        {
            for (var i = 0; i < 55; i++) { enumerated++; yield return new(i.ToString(), "title", "channel", "id", "image"); }
            await Task.CompletedTask;
        }
        var first = await pages.Get("source", 0, _ => Videos(), CancellationToken.None);
        Assert.Equal("20", first.NextPageToken); Assert.Equal(21, enumerated);
        var second = await pages.Get("source", 20, _ => Videos(), CancellationToken.None);
        Assert.Equal("20", second.Items[0].Id); Assert.Equal(41, enumerated);
        await pages.Get("source", 0, _ => Videos(), CancellationToken.None);
        Assert.Equal(41, enumerated);
        var last = await pages.Get("source", 40, _ => Videos(), CancellationToken.None);
        Assert.Equal(15, last.Items.Count); Assert.Null(last.NextPageToken); Assert.Equal(55, enumerated);
    }
    [Fact]
    public void StreamCacheUsesTheEarliestSignedExpiryWithSafetyMargin()
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = Extractor.StreamExpiry([
            "https://media.googlevideo.com/v?expire=" + now.AddHours(4).ToUnixTimeSeconds(),
            "https://media.googlevideo.com/v?expire=" + now.AddHours(2).ToUnixTimeSeconds()], now);
        Assert.InRange((expiry - now).TotalMinutes, 117.9, 118.1);
        Assert.Equal(now.AddMinutes(5), Extractor.StreamExpiry(["https://media.googlevideo.com/v"], now));
    }
    [Fact]
    public void MediaRangesUseProviderQueryWithoutRewritingSignedParameters()
    {
        const string url = "https://media.googlevideo.com/videoplayback?sig=a%2Bb&other=x%3Dy";
        Assert.Equal(url + "&range=42-99", Extractor.MediaRangeUri(url, 42, 99));
        Assert.Equal(url + "&range=10-20", Extractor.MediaRangeUri(url + "&range=0-9", 10, 20));
        Assert.Throws<ProviderException>(() => Extractor.MediaRangeUri("https://other.example/video", 0, 99));
        Assert.Throws<ProviderException>(() => Extractor.MediaRangeUri(url, 100, 99));
    }
    [Fact]
    public void ResumeSeekPointsContainOnlyVerifiedRandomAccessBoundaries()
    {
        var track = new AdaptiveTrack("", "avc1", 1000, 100, 1280, 720, 0, 1, 0, 1000, 0,
            [new(10, 19, 4000), new(20, 29, 2000, false), new(30, 39, 3000)]);
        Assert.Equal(new double[] { 0, 6 }, Extractor.SeekPoints(track));
    }
    [Fact]
    public async Task ThumbnailsSurviveRestartAndRespectTheirDiskBudget()
    {
        var factory = new ImageFactory();
        var directory = Path.Combine(_folder, "images");
        using (var cache = new ThumbnailCache(factory, directory, 8192))
        {
            await cache.Get("thumb000000", CancellationToken.None);
            await cache.Get("thumb000001", CancellationToken.None);
            await cache.Get("thumb000002", CancellationToken.None);
            Assert.True(new DirectoryInfo(directory).EnumerateFiles("*.jpg").Sum(file => file.Length) <= 8192);
            Assert.False(File.Exists(Path.Combine(directory, "thumb000000.jpg")));
        }
        var requests = factory.Requests;
        using (var reopened = new ThumbnailCache(factory, directory, 8192))
            Assert.Equal(4096, (await reopened.Get("thumb000002", CancellationToken.None))!.Length);
        Assert.Equal(requests, factory.Requests);
    }
    private sealed class ImageFactory : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) };
            response.Content.Headers.ContentType = new("image/jpeg");
            return Task.FromResult(response);
        }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
