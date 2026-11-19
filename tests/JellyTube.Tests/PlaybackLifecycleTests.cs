using System.Net;
using System.Text;
using Jellyfin.Plugin.JellyTube;
using Xunit;

namespace JellyTube.Tests;

public sealed class PlaybackLifecycleTests
{
    [Fact]
    public async Task MediaRedirectPreservesRangesWithoutForwardingCredentials()
    {
        using var backend = new RedirectBackend("https://cdn.googlevideo.com/media");
        using var client = new HttpClient(new MediaRedirectHandler { InnerHandler = backend });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://origin.googlevideo.com/media");
        request.Headers.Range = new(0, 1023);
        request.Headers.TryAddWithoutValidation("Authorization", "secret");
        request.Headers.TryAddWithoutValidation("Cookie", "secret");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(2, backend.Calls);
    }
    [Theory]
    [InlineData("http://cdn.googlevideo.com/media")]
    [InlineData("https://attacker.example/media")]
    [InlineData("https://googlevideo.com.attacker.example/media")]
    public async Task MediaRedirectRejectsUntrustedDestinations(string target)
    {
        using var backend = new RedirectBackend(target);
        using var client = new HttpClient(new MediaRedirectHandler { InnerHandler = backend });
        await Assert.ThrowsAsync<ProviderException>(() => client.GetAsync("https://origin.googlevideo.com/media"));
        Assert.Equal(1, backend.Calls);
    }
    [Fact]
    public async Task MediaRedirectLoopIsBounded()
    {
        using var backend = new RedirectBackend("https://origin.googlevideo.com/media", true);
        using var client = new HttpClient(new MediaRedirectHandler { InnerHandler = backend });
        await Assert.ThrowsAsync<ProviderException>(() => client.GetAsync("https://origin.googlevideo.com/media"));
        Assert.Equal(4, backend.Calls);
    }
    private sealed class RedirectBackend(string target, bool loop = false) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.False(request.Headers.Contains("Authorization")); Assert.False(request.Headers.Contains("Cookie"));
            if (Calls > 1 && !loop)
            {
                Assert.Equal("bytes=0-1023", request.Headers.Range?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[1024]) });
            }
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri(target);
            return Task.FromResult(response);
        }
    }
    [Fact]
    public async Task LastCancelledCallerStopsItsWorkerAndReleasesCapacity()
    {
        var requests = new SharedWork<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var capacity = new SemaphoreSlim(1);
        using var caller = new CancellationTokenSource();
        var pending = requests.Run("video", async ct =>
        {
            await capacity.WaitAsync(ct); entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return 1; }
            finally { capacity.Release(); released.SetResult(); }
        }, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, requests.Count); Assert.Equal(1, capacity.CurrentCount);
        Assert.Equal(42, await requests.Run("video", _ => Task.FromResult(42), CancellationToken.None));
    }
    [Fact]
    public async Task CancellingOneViewerDoesNotCancelAnotherViewer()
    {
        var requests = new SharedWork<int>();
        var complete = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new CancellationTokenSource();
        var calls = 0; CancellationToken worker = default;
        Task<int> Work(CancellationToken ct) { calls++; worker = ct; return complete.Task.WaitAsync(ct); }
        var a = requests.Run("same", Work, first.Token);
        var b = requests.Run("same", Work, CancellationToken.None);
        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(worker.IsCancellationRequested); Assert.Equal(1, calls);
        complete.SetResult(7); Assert.Equal(7, await b); Assert.Equal(0, requests.Count);
    }
    [Fact]
    public async Task FailedWorkerRetiresAndDoesNotPoisonTheNextVideo()
    {
        var requests = new SharedWork<int>();
        await Assert.ThrowsAsync<ProviderException>(() => requests.Run("bad", _ => Task.FromException<int>(new ProviderException("Unavailable")), CancellationToken.None));
        Assert.Equal(0, requests.Count);
        Assert.Equal(8, await requests.Run("healthy", _ => Task.FromResult(8), CancellationToken.None));
        Assert.Equal(9, await requests.Run("bad", _ => Task.FromResult(9), CancellationToken.None));
    }
    [Fact]
    public async Task ManifestDiscoverySkipsOnlyDeclaredLengthMetadataProbes()
    {
        using var backend = new EchoHandler();
        using var upstream = new HttpClient(backend);
        using var client = new HttpClient(new ManifestHttpHandler(upstream));
        using var probe = await client.GetAsync("https://media.googlevideo.com/videoplayback?clen=1000&range=998-999");
        Assert.Equal(HttpStatusCode.PartialContent, probe.StatusCode);
        Assert.Equal(2, probe.Content.Headers.ContentLength); Assert.Equal(1000, probe.Content.Headers.ContentRange?.Length);
        Assert.Equal(0, backend.Requests);
        using var media = await client.GetAsync("https://media.googlevideo.com/videoplayback?clen=1000&range=0-999");
        Assert.Equal("real media", await media.Content.ReadAsStringAsync()); Assert.Equal(1, backend.Requests);
        using var selected = new HttpRequestMessage(HttpMethod.Get, "https://media.googlevideo.com/videoplayback?clen=1000&range=998-999");
        selected.Headers.Range = new(998, 999);
        using var actual = await client.SendAsync(selected);
        Assert.Equal(2, backend.Requests);
        using var post = await client.PostAsync("https://www.youtube.com/youtubei/v1/player", new StringContent("player request", Encoding.UTF8, "application/json"));
        Assert.Equal("player request", backend.Body); Assert.Equal(3, backend.Requests);
    }
    [Theory]
    [InlineData("https://media.googlevideo.com/v?clen=1000&range=998-999", true)]
    [InlineData("https://media.googlevideo.com/v?clen=1000&range=999-999", false)]
    [InlineData("https://media.googlevideo.com/v?clen=1000&range=0-999", false)]
    [InlineData("https://media.googlevideo.com/v?range=998-999", false)]
    [InlineData("https://other.example/v?clen=1000&range=998-999", false)]
    public void MetadataProbeAdapterDoesNotRewriteMediaOrOtherHosts(string url, bool expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Assert.Equal(expected, ManifestHttpHandler.TryLengthProbe(request, out _));
    }
    private sealed class EchoHandler : HttpMessageHandler
    {
        public int Requests; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("real media") };
        }
    }
}
