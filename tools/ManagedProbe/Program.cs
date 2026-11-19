using System.Text.Json;
using Jellyfin.Plugin.JellyTube;
using Microsoft.Extensions.DependencyInjection;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
var services = new ServiceCollection();
services.AddHttpClient("JellyTube.YouTube", c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddHttpClient("JellyTube.Media", c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
using var provider = services.BuildServiceProvider();
var factory = provider.GetRequiredService<IHttpClientFactory>();
using var quick = new QuickFormats(factory);
var id = args.FirstOrDefault() ?? "aqz-KE-bpKQ";
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var manifest = args.Contains("--full") ? null : await quick.TryGet(id, 720, deadline.Token);
Console.WriteLine("quick=" + (manifest is not null));
manifest ??= await new YoutubeClient(factory.CreateClient("JellyTube.YouTube")).Videos.Streams.GetManifestAsync(id, deadline.Token);
var stream = manifest.GetVideoOnlyStreams().Where(s => s.Container.Name == "mp4" && s.VideoResolution.Height <= 720)
    .OrderByDescending(s => s.VideoResolution.Height).ThenByDescending(s => s.VideoCodec.StartsWith("avc")).First();
var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(stream.Url).Query);
Console.WriteLine(JsonSerializer.Serialize(new { stream.VideoCodec, height = stream.VideoResolution.Height, size = stream.Size.Bytes,
    existingRange = query.TryGetValue("range", out var r) ? r.ToString() : null,
    rangeSigned = query.TryGetValue("sparams", out var p) && p.ToString().Split(',').Contains("range") }));
foreach (var (from, to) in new[] { (0L, 524287L), (2248L, 89664L), (16110181L, 16664161L) })
foreach (var mode in new[] { "header", "query", "both" })
{
    var url = stream.Url;
    if (mode != "header") url += (url.Contains('?') ? "&" : "?") + $"range={from}-{to}";
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    if (mode != "query") request.Headers.Range = new(from, to);
    using var response = await factory.CreateClient("JellyTube.Media").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { mode, from, to, status = (int)response.StatusCode,
        length = response.Content.Headers.ContentLength, returned = response.Content.Headers.ContentRange?.ToString() }));
}
