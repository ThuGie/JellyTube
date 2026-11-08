using System.Buffers.Binary;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyTube;
using Xunit;

namespace JellyTube.Tests;

public sealed class CoreTests : IDisposable
{
    [Theory]
    [InlineData("", true)]
    [InlineData("?n=needs-deciphering", false)]
    public void QuickFormatsRequireClearOriginalAudio(string originalQuery, bool expected)
    {
        using var json = System.Text.Json.JsonDocument.Parse($$$"""
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"aqz-KE-bpKQ"},
             "streamingData":{"adaptiveFormats":[
               {"mimeType":"video/mp4; codecs=\"avc1.640028\"","url":"https://media.googlevideo.com/video",
                "contentLength":"100000","bitrate":900000,"width":1920,"height":1080,"fps":30},
               {"mimeType":"audio/mp4; codecs=\"mp4a.40.2\"","url":"https://media.googlevideo.com/dub",
                "contentLength":"10000","bitrate":128000,"audioTrack":{"id":"en.10","audioIsDefault":true}},
               {"mimeType":"audio/mp4; codecs=\"mp4a.40.2\"","url":"https://media.googlevideo.com/original{{{originalQuery}}}",
                "contentLength":"10000","bitrate":96000,"audioTrack":{"id":"es.4","displayName":"Spanish original"}}
             ]}}
            """);
        Assert.Equal(expected, QuickFormats.Parse(json.RootElement, "aqz-KE-bpKQ", 1080) is not null);
        Assert.Null(QuickFormats.Parse(json.RootElement, "8xfEpjgOzIA", 1080));
    }
    [Theory]
    [InlineData("Spanish (US) original", false, "", 4)]
    [InlineData("Spanish (US)", false, "?xtags=lang%3Des%3Aacont%3Doriginal", 4)]
    [InlineData("English (US)", true, "", 3)]
    [InlineData("English (US)", true, "?xtags=lang%3Den%3Aacont%3Ddubbed", 1)]
    [InlineData("Spanish original audio description", true, "", 0)]
    [InlineData(null, null, "", 2)]
    public void OriginalAudioOutranksLocalizedDefaults(string? name, bool? isDefault, string query, int expected) =>
        Assert.Equal(expected, Extractor.AudioPreference(name, isDefault, "https://media.googlevideo.com/videoplayback" + query));
    [Theory]
    [InlineData("music", "I built a garden shed", false)]
    [InlineData("music", "Building a shed with relaxing music", false)]
    [InlineData("music", "Live acoustic concert", true)]
    [InlineData("gaming", "Nintendo gameplay walkthrough", true)]
    [InlineData("gaming", "Live acoustic concert", false)]
    public void ExploreRequiresEvidenceOfTheSelectedTopic(string topic, string title, bool expected) =>
        Assert.Equal(expected, Catalog.MatchesExploreTopic(topic, title));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellytube-test-" + Guid.NewGuid().ToString("N"));
    private readonly Store _store;
    public CoreTests() { SQLitePCL.Batteries_V2.Init(); _store = new(Path.Combine(_directory, "test.db")); }
    [Fact]
    public void FavoritesAndSettingsAreOwnerScopedAndSurviveRestart()
    {
        _store.Put("alice", "favorite", "video", "Alice's private favorite");
        _store.Put("bob", "favorite", "video", "Bob's private favorite");
        _store.Put("alice", "settings", "current", new UserSettings(MaxHeight: 720));
        var reopened = new Store(Path.Combine(_directory, "test.db"));
        Assert.Equal("Alice's private favorite", reopened.Get<string>("alice", "favorite", "video"));
        Assert.Equal("Bob's private favorite", reopened.Get<string>("bob", "favorite", "video"));
        Assert.Equal(720, reopened.Settings("alice").MaxHeight); Assert.Equal(1080, reopened.Settings("bob").MaxHeight);
        reopened.Delete("alice", "favorite"); Assert.Single(reopened.List<string>("bob", "favorite"));
    }
    [Fact]
    public void PlaylistEditsRejectOtherOwnersAndStaleVersions()
    {
        var playlist = new PersonalPlaylist("p", "Private", []); _store.Put("alice", "playlist", "p", playlist);
        Assert.Throws<InvalidOperationException>(() => _store.SavePlaylist("bob", playlist));
        var updated = _store.SavePlaylist("alice", playlist with { Name = "Changed" }); Assert.Equal(2, updated.Version);
        Assert.Throws<InvalidOperationException>(() => _store.SavePlaylist("alice", playlist));
    }
    [Fact]
    public async Task ConcurrentQuotaClaimsCannotExceedBudget()
    {
        var accepted = 0; var now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        await Task.WhenAll(Enumerable.Range(0, 80).Select(i => Task.Run(() => { if (_store.ClaimSearch(i % 2 == 0 ? "alice" : "bob", 13, 8, now)) Interlocked.Increment(ref accepted); })));
        Assert.Equal(13, accepted);
        Assert.False(_store.ClaimSearch("new-user", 13, 8, now));
        Assert.True(_store.ClaimSearch("alice", 13, 8, now.AddDays(1)));
    }
    [Fact]
    public void QuotasResetAtPacificMidnightRatherThanUtcMidnight()
    {
        Assert.True(_store.ClaimSearch("alice", 1, 1, DateTimeOffset.Parse("2026-11-07T23:00:00Z")));
        Assert.False(_store.ClaimSearch("alice", 1, 1, DateTimeOffset.Parse("2026-11-08T01:00:00Z")));
        Assert.True(_store.ClaimSearch("alice", 1, 1, DateTimeOffset.Parse("2026-11-08T09:00:00Z")));
    }
    [Theory]
    [InlineData("https://r1---sn.example.googlevideo.com/videoplayback", true)]
    [InlineData("https://googlevideo.com.evil.example/video", false)]
    [InlineData("https://evilgooglevideo.com/video", false)]
    [InlineData("http://r1.googlevideo.com/video", false)]
    [InlineData("https://r1.googlevideo.com:444/video", false)]
    [InlineData("https://admin@r1.googlevideo.com/video", false)]
    [InlineData("http://127.0.0.1/secrets", false)]
    public void MediaRelayIsLimitedToHttpsGoogleVideoDestinations(string value, bool expected) => Assert.Equal(expected, Extractor.AllowedMediaUri(value));
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ", true)]
    [InlineData("https://youtu.be/aqz-KE-bpKQ?t=20", true)]
    [InlineData("https://www.youtube.com/shorts/aqz-KE-bpKQ", true)]
    [InlineData("aqz-KE-bpKQ", true)]
    [InlineData("https://youtube.com.evil.test/watch?v=aqz-KE-bpKQ", false)]
    [InlineData("file:///etc/passwd", false)]
    public void VideoUrlParsingNeverAcceptsArbitraryHosts(string value, bool expected) => Assert.Equal(expected, JellyTubeController.TryVideo(value, out _));
    [Fact]
    public void Mp4IndexFindsRangesAndRejectsTruncatedOrInvalidBoxes()
    {
        var data = new byte[64]; BinaryPrimitives.WriteUInt32BigEndian(data, 16); "ftyp"u8.CopyTo(data.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), 16); "moov"u8.CopyTo(data.AsSpan(20));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(32), 32); "sidx"u8.CopyTo(data.AsSpan(36));
        Assert.Equal((32L, 63L, 31L), Mp4Index.Read(data));
        Assert.Throws<ProviderException>(() => Mp4Index.Read(data.AsSpan(0, 40)));
        BinaryPrimitives.WriteUInt32BigEndian(data, 0); Assert.Throws<ProviderException>(() => Mp4Index.Read(data));
    }
    [Fact]
    public void ManifestContainsBothTracksAndSeekIndexRangesWithoutUpstreamUrls()
    {
        var video = new AdaptiveTrack("https://secret.googlevideo.com/signed", "avc1.640028", 10000, 2000000, 1920, 1080, 700, 1000, 699);
        var audio = new AdaptiveTrack("https://secret.googlevideo.com/audio", "mp4a.40.2", 5000, 128000, 0, 0, 600, 800, 599);
        var xml = MediaController.ManifestXml(new(video, audio, DateTimeOffset.UtcNow), 125, "https://server/tube-test/JellyTube/Media/capability/");
        var doc = XDocument.Parse(xml); XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        Assert.Equal(2, doc.Descendants(ns + "AdaptationSet").Count());
        Assert.Contains("700-1000", xml); Assert.Contains("video.mp4", xml); Assert.Contains("audio.mp4", xml);
        Assert.DoesNotContain("googlevideo", xml); Assert.Contains("PT2M5S", xml);
    }
    [Fact]
    public void FilePathsCannotEscapeManagedRoot()
    {
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => Rules.Inside(_directory, "../other/file.mp4"));
        Assert.StartsWith(_directory, Rules.Inside(_directory, "owner/video.mp4"));
        Assert.True(Rules.PathsOverlap(_directory, Path.Combine(_directory, "owner")));
        Assert.True(Rules.PathsOverlap(Path.Combine(_directory, "owner"), _directory));
        Assert.False(Rules.PathsOverlap(Path.Combine(_directory, "alice"), Path.Combine(_directory, "alice-other")));
        Assert.True(Rules.PathsOverlap(Path.GetPathRoot(_directory)!, _directory));
    }
    [Fact]
    public void LibraryPrivacyChecksBodyAndQueryPathRepresentations()
    {
        var root = Path.Combine(_directory, "owner");
        Assert.Equal(new[] { root }, LibraryPrivacyFilter.MediaPaths(new[] { root }, 0));
        Assert.Equal(new[] { root }, LibraryPrivacyFilter.MediaPaths(new { LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions { PathInfos = [new() { Path = root }] } }, 0));
        Assert.Equal(new[] { root }, LibraryPrivacyFilter.MediaPaths(new { PathInfo = new MediaBrowser.Model.Configuration.MediaPathInfo { Path = root } }, 0));
        Assert.Empty(LibraryPrivacyFilter.MediaPaths("not-a-path-argument", 0));
    }
    [Fact]
    public void SegmentListUsesSidxTimingAndExactByteRanges()
    {
        var bytes = new byte[60]; BinaryPrimitives.WriteUInt32BigEndian(bytes, 16); "ftyp"u8.CopyTo(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 44); "sidx"u8.CopyTo(bytes.AsSpan(20));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32), 1000); // timescale
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36), 2000); // earliest presentation
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), 100); // first media offset after sidx
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(46), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(48), 500);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(52), 5000);
        var parsed = Mp4Index.Segments(bytes);
        Assert.Equal(1000U, parsed.Timescale); Assert.Equal(2000UL, parsed.PresentationTime);
        Assert.Equal(new MediaSegment(160, 659, 5000), Assert.Single(parsed.Segments));
        var track = new AdaptiveTrack("https://hidden.googlevideo.com/media", "avc1.4D4020", 1000, 10000, 640, 360, parsed.Start, parsed.End, parsed.InitEnd, parsed.Timescale, parsed.PresentationTime, parsed.Segments);
        var xml = MediaController.ManifestXml(new(track, track, DateTimeOffset.UtcNow), 5, "https://server/protected/");
        Assert.Contains("SegmentList", xml); Assert.Contains("mediaRange=\"160-659\"", xml); Assert.Contains("presentationTimeOffset=\"2000\"", xml);
        Assert.Contains("height=720", xml); Assert.DoesNotContain("googlevideo", xml);
        Assert.NotEqual(MediaController.Fingerprint(track with { IndexIdentity = "layout-a" }), MediaController.Fingerprint(track with { IndexIdentity = "layout-b" }));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(48), 0x80000001);
        Assert.Throws<ProviderException>(() => Mp4Index.Segments(bytes));
    }
    [Fact]
    public void PluginConfigurationRoundTripsWithJellyfinXmlSerialization()
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Configuration));
        var config = new Configuration { Libraries = [new("alice", Guid.NewGuid().ToString("N"), "/volume1/YouTube/alice")] };
        using var writer = new StringWriter(); serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString()); var restored = (Configuration)serializer.Deserialize(reader)!;
        Assert.Equal(config.Libraries[0].Root, restored.Libraries[0].Root);
    }
    [Fact]
    public void SegmentCacheSeparatesFormatsAndDoesNotRetainOversizeRanges()
    {
        using var cache = new SegmentCache(); cache.Put("video|720|v1|0-3", [1, 2, 3, 4]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, cache.Get("video|720|v1|0-3"));
        Assert.Null(cache.Get("video|1080|v1|0-3")); cache.Put("large", new byte[4 * 1024 * 1024 + 1]); Assert.Null(cache.Get("large"));
    }
    [Fact]
    public async Task StandaloneNavigationPreservesOtherPluginsAndUsesCorrectContentLength()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Path = "/base/web/index.html";
        context.Request.Headers.AcceptEncoding = "gzip, br"; context.Request.Headers.IfNoneMatch = "old-index";
        using var body = new MemoryStream(); context.Response.Body = body;
        var middleware = new NavigationMiddleware(async ctx =>
        {
            Assert.Equal(0, ctx.Request.Headers.AcceptEncoding.Count); Assert.Equal(0, ctx.Request.Headers.IfNoneMatch.Count);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var html = "<html><head><title>Jellyfin</title></head><body><script data-other-plugin src=\"other.js\"></script></body></html>";
            await ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(html));
        });
        await middleware.InvokeAsync(context);
        var result = System.Text.Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("data-other-plugin", result); Assert.Contains("../JellyTube/assets/app.js", result);
        Assert.Contains("../JellyTube/assets/app.css", result); Assert.Equal(body.Length, context.Response.ContentLength);
        Assert.Equal("gzip, br", context.Request.Headers.AcceptEncoding.ToString()); Assert.Same(body, context.Response.Body);
        Assert.Equal(result, WebTransformation.IndexHtml(new() { Contents = result }));
    }
    [Fact]
    public async Task NavigationNeverBuffersOrChangesMediaResponses()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Path = "/base/JellyTube/Media/video.mp4";
        context.Request.Headers.AcceptEncoding = "gzip"; using var body = new MemoryStream(); context.Response.Body = body;
        var middleware = new NavigationMiddleware(async ctx =>
        {
            Assert.Same(body, ctx.Response.Body); Assert.Equal("gzip", ctx.Request.Headers.AcceptEncoding.ToString());
            ctx.Response.ContentType = "video/mp4"; await ctx.Response.Body.WriteAsync(new byte[] { 1, 2, 3 });
        });
        await middleware.InvokeAsync(context); Assert.Equal(new byte[] { 1, 2, 3 }, body.ToArray());
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
