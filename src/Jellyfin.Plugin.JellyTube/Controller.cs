using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube;

public sealed class JellyTubeErrors : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.HttpContext.Response.HasStarted)
        {
            context.ExceptionHandled = true; context.HttpContext.Abort(); return;
        }
        context.HttpContext.Response.Headers.CacheControl = "private, no-store";
        var ex = context.Exception;
        var status = ex switch
        {
            UnauthorizedAccessException => 403, KeyNotFoundException => 404,
            ValidationException or ArgumentException => 400, InvalidOperationException => 409,
            ProviderException => 502, OperationCanceledException => 408, _ => 500
        };
        var error = ex.Message;
        if (status == 500)
        {
            var reference = context.HttpContext.TraceIdentifier;
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<JellyTubeErrors>>();
            // Exception messages and request URLs can contain stream capabilities or API credentials.
            for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
                logger.LogError("JellyTube request {Reference} failed in {Action} with {ExceptionType} ({ErrorCode:X8}). {StackTrace}",
                    reference, context.ActionDescriptor.DisplayName, cause.GetType().FullName, cause.HResult, cause.StackTrace);
            error = ex is IOException && context.RouteData.Values["action"] as string == nameof(JellyTubeController.Prepare)
                ? $"JellyTube could not read or write the playback files. Check that the Jellyfin package account can write to the configured media folder. Log reference {reference}."
                : $"JellyTube could not complete this request. Check the Jellyfin server log for reference {reference}.";
        }
        context.Result = new JsonResult(new { error }, Store.Json) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
public sealed class JellyTubeJson : ResultFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        context.HttpContext.Response.Headers.CacheControl = "private, no-store";
        if (context.Result is ObjectResult result) context.Result = new JsonResult(result.Value, Store.Json) { StatusCode = result.StatusCode ?? 200 };
    }
}
public sealed record NameRequest(string Name);
public sealed record ProgressRequest(double PositionSeconds);
public sealed record AdminSettings(string PublicBaseUrl, string MediaRoot, long StorageBudgetMb, bool EnableDownloads,
    int SearchCallsPerDay, int UserSearchCallsPerDay, string[] DeniedUsers, string? ApiKey = null, string[]? DownloadDeniedUsers = null, string MinimumSafeSearch = "moderate");

[ApiController, Route("JellyTube"), Authorize, JellyTubeErrors, JellyTubeJson]
public sealed class JellyTubeController(Store store, Access access, Catalog catalog, Extractor extractor, NativeLibrary native, Downloads downloads, JellyfinMuxer muxer, NativeUserData nativeUserData, LibraryPrivacy privacy) : ControllerBase
{
    private string Owner()
    {
        var ids = User.Claims.Where(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase)).Select(c => Guid.TryParse(c.Value, out var v) ? v : Guid.Empty).Distinct().ToArray();
        if (ids.Length != 1 || ids[0] == Guid.Empty) throw new UnauthorizedAccessException("A Jellyfin user session is required.");
        var owner = Rules.Owner(ids[0]); access.User(owner); return owner;
    }
    private async Task<VideoInfo> Video(string owner, string id, CancellationToken ct)
    {
        Rules.VideoId(id);
        var known = store.Get<VideoInfo>(owner, "video", id) ?? store.Get<MediaAsset>(owner, "asset", id)?.Video;
        if (known is not null) return known;
        var result = await extractor.Details(id, ct); store.Put(owner, "video", id, result); return result;
    }
    [HttpGet("assets/{name}"), AllowAnonymous]
    public ActionResult Asset(string name) => name switch
    {
        "app.js" => Content(Plugin.Resource("app.js"), "application/javascript"),
        "app.css" => Content(Plugin.Resource("app.css"), "text/css"), _ => NotFound()
    };
    [HttpGet("Session")]
    public ActionResult Session()
    {
        var owner = Owner();
        var config = Plugin.Instance.Configuration;
        var binding = access.CanUse(owner) ? config.SharedLibrary ?? config.Libraries.FirstOrDefault(l => l.UserId == owner) : null;
        return Ok(new { userId = owner, admin = access.IsAdmin(owner), nativeLibrary = binding is null ? null : new { libraryId = binding.LibraryId },
            downloads = Plugin.Instance.Configuration.EnableDownloads && !Plugin.Instance.Configuration.DownloadDeniedUsers.Contains(owner, StringComparer.OrdinalIgnoreCase), settings = store.Settings(owner),
            minimumSafeSearch = Plugin.Instance.Configuration.MinimumSafeSearch,
            discovery = string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.ApiKey) ? "managed" : "official",
            safeSearchAvailable = !string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.ApiKey), version = typeof(Plugin).Assembly.GetName().Version?.ToString() });
    }
    [HttpGet("Search")]
    public async Task<ActionResult> Search([FromQuery] string query, [FromQuery] string? page, CancellationToken ct)
    {
        var owner = Owner();
        if (TryVideo(query, out var id)) return Ok(new CatalogPage([await Video(owner, id, ct)]));
        var result = await catalog.Search(owner, query, page, ct);
        foreach (var video in result.Items) store.Put(owner, "video", video.Id, video);
        // Bound per-user discovery snapshots without touching saved collections.
        var stale = store.List<VideoInfo>(owner, "video"); if (stale.Count > 1000) foreach (var v in stale.Take(stale.Count - 1000)) store.Delete(owner, "video", v.Id);
        return Ok(result);
    }
    internal static bool TryVideo(string text, out string id)
    {
        id = text.Trim(); try { Rules.VideoId(id); return true; } catch (ValidationException) { }
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) id = uri.AbsolutePath.Trim('/');
        else if (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal)) id = uri.AbsolutePath.Split('/').Last();
            else id = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("v", out var value) ? value.ToString() : "";
        }
        else return false;
        try { Rules.VideoId(id); return true; } catch (ValidationException) { return false; }
    }
    [HttpPost("Videos/{id}/Prepare")]
    public async Task<ActionResult> Prepare(string id, CancellationToken ct)
    {
        var owner = Owner();
        access.Binding(owner);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Rules.VideoId(id);
        var height = Plugin.Instance.Configuration.SharedLibrary is null ? store.Settings(owner).MaxHeight : MediaController.SharedStreamHeight;
        if (downloads.PlaybackFile(id, height, store.Get<MediaAsset>(owner, "asset", id)) is null)
            _ = extractor.WarmAdaptive(id, height);
        var video = await Video(owner, id, ct);
        var asset = await native.Prepare(owner, video, ct);
        var prepareMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        HttpContext.RequestServices.GetRequiredService<ILogger<JellyTubeController>>()
            .LogInformation("JellyTube playback preparation {Reference} completed in {PrepareMs:F0} ms.", HttpContext.TraceIdentifier, prepareMs);
        return Ok(new { video = asset.Video, itemId = asset.ItemId, positionTicks = nativeUserData.PlaybackPosition(owner, asset.ItemId),
            reference = HttpContext.TraceIdentifier, prepareMs, status = "Ready" });
    }
    [HttpGet("Videos/{id}/Details")]
    public async Task<ActionResult> Details(string id, CancellationToken ct)
    {
        var owner = Owner();
        var details = await extractor.Details(Rules.VideoId(id), ct);
        store.Put(owner, "video", id, details);
        return Ok(details);
    }
    [HttpGet("Discover")]
    public async Task<ActionResult> Discover([FromQuery] string query, [FromQuery] string kind, CancellationToken ct)
    {
        var owner = Owner(); query = query.Trim(); if (query.Length is < 2 or > 150) throw new ValidationException("Enter a search between 2 and 150 characters.");
        var c = Plugin.Instance.Configuration;
        if (!store.ClaimSearch(owner, c.SearchCallsPerDay, c.UserSearchCallsPerDay, DateTimeOffset.UtcNow)) throw new ProviderException("Today's search budget has been reached.");
        return Ok(new { items = await extractor.Discover(query, kind, ct) });
    }
    [HttpGet("Playlists/{id}/Videos")]
    public async Task<ActionResult> PlaylistVideos(string id, CancellationToken ct, [FromQuery] string? page = null)
    {
        var owner = Owner(); var result = await extractor.PlaylistVideos(id, ct, page);
        foreach (var v in result.Items) store.Put(owner, "video", v.Id, v); return Ok(result);
    }
    [HttpGet("Me/Library")]
    public async Task<ActionResult> Library(CancellationToken ct)
    {
        var owner = Owner(); await nativeUserData.Synchronize(owner, ct); return Ok(new
        {
            favorites = store.List<SavedVideo>(owner, "favorite").OrderByDescending(v => v.AddedAt),
            watchLater = store.List<SavedVideo>(owner, "later").OrderByDescending(v => v.AddedAt),
            playlists = store.List<PersonalPlaylist>(owner, "playlist"), channels = store.List<SavedChannel>(owner, "channel"), history = store.List<HistoryEntry>(owner, "history").OrderByDescending(v => v.WatchedAt).Take(100)
        });
    }
    [HttpPut("Me/{kind:regex(^(Favorites|WatchLater)$)}/{id}")]
    public async Task<ActionResult> Save(string kind, string id, CancellationToken ct)
    {
        var owner = Owner(); var video = await Video(owner, id, ct);
        store.Put(owner, kind == "Favorites" ? "favorite" : "later", id, new SavedVideo(video, DateTimeOffset.UtcNow));
        if (access.CanUse(owner)) await native.Prepare(owner, video, ct);
        if (kind == "Favorites") await nativeUserData.SetFavorite(owner, id, true, ct); return NoContent();
    }
    [HttpGet("Me/Recommendations/Topics")]
    public async Task<ActionResult> TopicRecommendations(CancellationToken ct) =>
        Ok(await catalog.RecommendTopics(Owner(), ct));
    [HttpGet("Explore")]
    public async Task<ActionResult> Explore([FromQuery] string topic, [FromQuery] string? page, CancellationToken ct)
    {
        var owner = Owner();
        var result = await catalog.Explore(owner, topic, page, ct);
        foreach (var video in result.Items) store.Put(owner, "video", video.Id, video);
        return Ok(result);
    }
    [HttpDelete("Me/{kind:regex(^(Favorites|WatchLater)$)}/{id}")]
    public async Task<ActionResult> Unsave(string kind, string id, CancellationToken ct)
    {
        var owner = Owner(); store.Delete(owner, kind == "Favorites" ? "favorite" : "later", Rules.VideoId(id));
        if (kind == "Favorites") await nativeUserData.SetFavorite(owner, id, false, ct); return NoContent();
    }
    [HttpGet("Channels/{id}/Uploads")]
    public async Task<ActionResult> Uploads(string id, CancellationToken ct, [FromQuery] string? page = null)
    {
        var owner = Owner(); var result = await extractor.Uploads(id, ct, page);
        foreach (var v in result.Items) store.Put(owner, "video", v.Id, v); return Ok(result);
    }
    [HttpPut("Me/Channels/{id}")]
    public async Task<ActionResult> Follow(string id, CancellationToken ct)
    {
        var owner = Owner(); store.Put(owner, "channel", id, new SavedChannel(await extractor.Channel(id, ct), DateTimeOffset.UtcNow)); return NoContent();
    }
    [HttpDelete("Me/Channels/{id}")]
    public ActionResult Unfollow(string id) { store.Delete(Owner(), "channel", id); return NoContent(); }
    [HttpGet("Me/Settings")]
    public ActionResult Settings() => Ok(store.Settings(Owner()));
    [HttpPut("Me/Settings")]
    public ActionResult Settings([FromBody] UserSettings settings)
    {
        var owner = Owner(); store.Put(owner, "settings", "current", Rules.Settings(settings));
        if (!settings.RecordHistory) ClearWatchHistory(owner);
        return Ok(settings);
    }
    [HttpPost("Me/Playlists")]
    public ActionResult CreatePlaylist([FromBody] NameRequest request)
    {
        var owner = Owner(); if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120) throw new ValidationException("Enter a playlist name of at most 120 characters.");
        var playlist = new PersonalPlaylist(Guid.NewGuid().ToString("N"), request.Name.Trim(), []); store.Put(owner, "playlist", playlist.Id, playlist); return Ok(playlist);
    }
    [HttpPut("Me/Playlists/{id}")]
    public ActionResult UpdatePlaylist(string id, [FromBody] PersonalPlaylist value)
    {
        if (id != value.Id) throw new ValidationException("Playlist ID mismatch."); return Ok(store.SavePlaylist(Owner(), value));
    }
    [HttpDelete("Me/Playlists/{id}")]
    public ActionResult RemovePlaylist(string id) { store.Delete(Owner(), "playlist", id); return NoContent(); }
    [HttpPut("Me/History/{id}")]
    public async Task<ActionResult> Progress(string id, [FromBody] ProgressRequest progress, CancellationToken ct)
    {
        var owner = Owner(); if (!store.Settings(owner).RecordHistory) return NoContent();
        if (!double.IsFinite(progress.PositionSeconds) || progress.PositionSeconds < 0) throw new ValidationException("Invalid playback position.");
        store.Put(owner, "history", Rules.VideoId(id), new HistoryEntry(await Video(owner, id, ct), progress.PositionSeconds, DateTimeOffset.UtcNow)); return NoContent();
    }
    [HttpDelete("Me/History")]
    public ActionResult ClearHistory()
    {
        ClearWatchHistory(Owner());
        return NoContent();
    }

    private void ClearWatchHistory(string owner)
    {
        store.Put(owner, "historyClear", "current", DateTimeOffset.UtcNow);
        store.Delete(owner, "history");
        store.Delete(owner, "topicRecommendations");
    }
    [HttpGet("Me/Downloads")]
    public ActionResult Jobs() => Ok(store.List<DownloadJob>(Owner(), "download").OrderByDescending(j => j.UpdatedAt));
    [HttpPost("Me/Downloads/{id}")]
    public async Task<ActionResult> Queue(string id, CancellationToken ct)
    {
        var owner = Owner(); var c = Plugin.Instance.Configuration;
        if (!c.EnableDownloads || c.DownloadDeniedUsers.Contains(owner, StringComparer.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Downloads are disabled for this account.");
        return Accepted(downloads.Enqueue(owner, await Video(owner, id, ct)));
    }
    [HttpDelete("Me/Downloads/{id}")]
    public ActionResult Cancel(string id) { downloads.Cancel(Owner(), Rules.VideoId(id)); return NoContent(); }
    [HttpDelete("Me/Retained/{id}")]
    public ActionResult RemoveRetained(string id)
    {
        var owner = Owner(); Rules.VideoId(id); access.Binding(owner);
        downloads.RemoveRetained(owner, id);
        return NoContent();
    }
    [HttpGet("Me/Export")]
    public ActionResult Export()
    {
        var owner = Owner(); return Ok(new { settings = store.Settings(owner), favorites = store.List<SavedVideo>(owner, "favorite"), watchLater = store.List<SavedVideo>(owner, "later"), playlists = store.List<PersonalPlaylist>(owner, "playlist"), channels = store.List<SavedChannel>(owner, "channel"), history = store.List<HistoryEntry>(owner, "history") });
    }
    [HttpDelete("Me/Data")]
    public ActionResult ClearData()
    {
        var owner = Owner(); foreach (var kind in new[] { "favorite", "favoriteSync", "later", "playlist", "channel", "history", "video", "settings" }) store.Delete(owner, kind);
        ClearWatchHistory(owner);
        return NoContent(); // Retained media and native Jellyfin user data have their own explicit management.
    }
    [HttpGet("Admin/Settings"), Authorize(Policy = "RequiresElevation")]
    public ActionResult AdminConfig()
    {
        Owner(); var c = Plugin.Instance.Configuration;
        return Ok(new { c.PublicBaseUrl, mediaRoot = native.MediaRoot, c.StorageBudgetMb, c.EnableDownloads, c.SearchCallsPerDay, c.UserSearchCallsPerDay,
            c.DeniedUsers, c.DownloadDeniedUsers, c.MinimumSafeSearch, apiKeyPresent = !string.IsNullOrWhiteSpace(c.ApiKey),
            sharedLibrary = c.SharedLibrary, legacyLibraryCount = c.Libraries.Count,
            users = access.Users().Select(u => new { id = Rules.Owner(u.Id), name = u.Username, admin = access.Administrator(u) }) });
    }
    [HttpPut("Admin/Settings"), Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> AdminConfig([FromBody] AdminSettings request, CancellationToken ct)
    {
        Owner(); var c = Plugin.Instance.Configuration;
        if (request.StorageBudgetMb is < 256 or > 10_485_760 || request.SearchCallsPerDay is < 1 or > 100000 || request.UserSearchCallsPerDay < 1)
            throw new ValidationException("Invalid storage or search limits.");
        if (!Uri.TryCreate(request.PublicBaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new ValidationException("Enter the Jellyfin URL reachable by your clients, including any reverse-proxy base path.");
        if ((c.Libraries.Count > 0 || c.SharedLibrary is not null) && !Path.GetFullPath(request.MediaRoot).Equals(native.MediaRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ValidationException("The media root cannot be changed while a JellyTube library exists.");
        access.CheckMediaRoot(request.MediaRoot);
        foreach (var id in request.DeniedUsers) if (!Guid.TryParseExact(id, "N", out _)) throw new ValidationException("Invalid denied user ID.");
        foreach (var id in request.DownloadDeniedUsers ?? []) if (!Guid.TryParseExact(id, "N", out _)) throw new ValidationException("Invalid download user ID.");
        if (request.MinimumSafeSearch is not ("strict" or "moderate")) throw new ValidationException("Invalid minimum SafeSearch setting.");
        var changedUrl = c.PublicBaseUrl != request.PublicBaseUrl.TrimEnd('/');
        var reenabled = c.DeniedUsers.Except(request.DeniedUsers, StringComparer.OrdinalIgnoreCase).ToArray();
        c.PublicBaseUrl = request.PublicBaseUrl.TrimEnd('/'); c.MediaRoot = Path.GetFullPath(request.MediaRoot);
        c.StorageBudgetMb = request.StorageBudgetMb; c.EnableDownloads = request.EnableDownloads; c.SearchCallsPerDay = request.SearchCallsPerDay;
        c.UserSearchCallsPerDay = request.UserSearchCallsPerDay; c.DeniedUsers = request.DeniedUsers.Select(id => Guid.Parse(id).ToString("N")).Distinct().ToArray();
        c.DownloadDeniedUsers = (request.DownloadDeniedUsers ?? c.DownloadDeniedUsers).Select(id => Guid.Parse(id).ToString("N")).Distinct().ToArray(); c.MinimumSafeSearch = request.MinimumSafeSearch;
        if (request.ApiKey is not null) c.ApiKey = request.ApiKey.Trim(); Plugin.Instance.UpdateConfiguration(c);
        await native.RestoreSharedAccess(reenabled);
        await privacy.Enforce();
        if (changedUrl) await native.RewriteUrls(ct); return NoContent();
    }
    [HttpPost("Admin/Library"), Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Provision(CancellationToken ct)
    { Owner(); return Ok(await native.Provision(ct)); }
    [HttpGet("Admin/Health"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Health()
    {
        Owner(); return Ok(new { extractor = "Managed C# / YoutubeExplode 6.6.3", externalDownloaderRequired = false,
            ffmpeg = muxer.EncoderPath, ffmpegAvailable = System.IO.File.Exists(muxer.EncoderPath), navigation = NavigationMiddleware.Registered || WebTransformation.Registered,
            storageBytes = downloads.StorageBytes(), sharedBytes = downloads.SharedBytes(),
            mediaUrlConfigured = Uri.TryCreate(Plugin.Instance.Configuration.PublicBaseUrl, UriKind.Absolute, out var mediaUrl)
                && mediaUrl.Scheme is "http" or "https" && string.IsNullOrEmpty(mediaUrl.UserInfo)
                && string.IsNullOrEmpty(mediaUrl.Query) && string.IsNullOrEmpty(mediaUrl.Fragment),
            sharedLibraryAvailable = Plugin.Instance.Configuration.SharedLibrary is { } binding && Directory.Exists(binding.Root) });
    }
}
