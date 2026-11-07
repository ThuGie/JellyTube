using System.Reflection;
using System.Runtime.Loader;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.JellyTube;

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) { Instance = this; }
    public override string Name => "JellyTube";
    public override Guid Id => Guid.Parse("241ac687-789b-4499-8ba1-9fb16602b421");
    public override string Description => "YouTube discovery, personal libraries and native Jellyfin playback. Managed extraction; no external downloader.";
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new() { Name = "JellyTube", DisplayName = Name, EnableInMainMenu = true, MenuIcon = "smart_display", EmbeddedResourcePath = "Jellyfin.Plugin.JellyTube.Web.config.html" };
    }
    public static string Resource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyTube.Web." + name) ?? throw new FileNotFoundException();
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, JellyTubeStartupFilter>();
        services.AddHttpClient("JellyTube.Catalog", c => c.Timeout = TimeSpan.FromSeconds(20)).RemoveAllLoggers();
        services.AddHttpClient("JellyTube.YouTube", c => c.Timeout = TimeSpan.FromSeconds(45)).RemoveAllLoggers();
        services.AddHttpClient("JellyTube.Media", c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = System.Net.DecompressionMethods.None }).RemoveAllLoggers();
        services.AddSingleton<Store>(); services.AddSingleton<JellyfinMuxer>(); services.AddSingleton<Extractor>();
        services.AddSingleton<Catalog>(); services.AddSingleton<Access>(); services.AddSingleton<NativeLibrary>();
        services.AddSingleton<NativeUserData>();
        services.AddSingleton<SegmentCache>();
        services.AddSingleton<ThumbnailCache>();
        services.AddSingleton<LibraryPrivacy>();
        services.AddSingleton<MediaBrowser.Controller.Events.IEventConsumer<Jellyfin.Data.Events.Users.UserCreatedEventArgs>, UserCreatedConsumer>();
        services.AddScoped<LibraryPrivacyFilter>();
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<LibraryPrivacyFilter>());
        services.AddSingleton<Downloads>(); services.AddHostedService(p => p.GetRequiredService<Downloads>());
        services.AddHostedService<InjectionService>();
    }
}

public sealed class PatchPayload { public string? Contents { get; set; } }
public static class WebTransformation
{
    public static bool Registered { get; private set; }
    public static string IndexHtml(PatchPayload payload)
    {
        var html = payload.Contents ?? "";
        if (html.Contains("data-jellytube-assets", StringComparison.Ordinal)) return html;
        var revision = typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        return html.Replace("</head>", $"<link data-jellytube-assets rel=\"stylesheet\" href=\"../JellyTube/assets/app.css?v={revision}\"></head>", StringComparison.Ordinal)
            .Replace("</body>", $"<script defer data-jellytube-assets src=\"../JellyTube/assets/app.js?v={revision}\"></script></body>", StringComparison.Ordinal);
    }
    public static bool Register()
    {
        var type = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).Select(a => a.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")).FirstOrDefault(t => t != null);
        var method = type?.GetMethod("RegisterTransformation"); if (method is null) return false;
        method.Invoke(null, [new JObject { ["id"] = "ebdbfb50-60f5-4130-9031-442b001d9ae2", ["fileNamePattern"] = "index.html", ["callbackAssembly"] = typeof(WebTransformation).Assembly.FullName, ["callbackClass"] = typeof(WebTransformation).FullName, ["callbackMethod"] = nameof(IndexHtml) }]);
        return Registered = true;
    }
}
public sealed class InjectionService(ILogger<InjectionService> logger, NativeLibrary native) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { native.DisableSharedLibraryWatcher(); }
        catch (Exception ex) { logger.LogWarning("JellyTube could not disable its library watcher ({ErrorType}).", ex.GetType().Name); }
        for (var i = 0; i < 30; i++)
        {
            try { if (NavigationMiddleware.Registered || WebTransformation.Register()) return; }
            catch (Exception) { logger.LogDebug("JellyTube navigation registration is not ready."); }
            await Task.Delay(2000, stoppingToken);
        }
        logger.LogInformation("JellyTube native libraries remain available. Install File Transformation for the YouTube discovery interface.");
    }
}
