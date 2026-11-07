using System.Text.Json;
using Jellyfin.Plugin.JellyTube;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHttpClient("JellyTube.YouTube", c => c.Timeout = TimeSpan.FromSeconds(45));
services.AddHttpClient("JellyTube.Media", c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
using var provider = services.BuildServiceProvider();
var extractor = new Extractor(provider.GetRequiredService<IHttpClientFactory>());
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var id = args.FirstOrDefault() ?? "aqz-KE-bpKQ";
var result = new Dictionary<string, object?>();
try { var info = await extractor.Details(id, deadline.Token); result["metadata"] = new { info.Id, info.Title, info.DurationSeconds }; }
catch (Exception ex) { result["metadataError"] = ex.Message; }
try { var page = await extractor.Search("Blender Big Buck Bunny", deadline.Token); result["searchCount"] = page.Items.Count; }
catch (Exception ex) { result["searchError"] = ex.Message; }
try
{
    var source = await extractor.ResolveAdaptive(id, 720, deadline.Token);
    result["stream"] = new { video = new { source.Video.Codec, source.Video.Width, source.Video.Height, source.Video.IndexStart, source.Video.IndexEnd }, audio = new { source.Audio.Codec, source.Audio.IndexStart, source.Audio.IndexEnd } };
}
catch (Exception ex) { result["streamError"] = ex.Message; }
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return result.ContainsKey("streamError") ? 1 : 0;
