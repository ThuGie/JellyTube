using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.JellyTube;

public sealed class NativeLibrary(Store store, Access access, ILibraryManager library, IUserManager users)
{
    private readonly SemaphoreSlim _provision = new(1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scans = new();
    public string MediaRoot => string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.MediaRoot)
        ? Path.Combine(Plugin.Instance.DataFolderPath, "media") : Path.GetFullPath(Plugin.Instance.Configuration.MediaRoot);

    /// <summary>Explicit administrator action. Preserves existing library access while making the new library owner-only.</summary>
    public async Task<LibraryBinding> Provision(string owner, CancellationToken ct)
    {
        var user = access.User(owner);
        await _provision.WaitAsync(ct);
        try
        {
            var config = Plugin.Instance.Configuration;
            if (config.Libraries.Any(x => x.UserId == owner)) return access.Binding(owner);
            access.CheckMediaRoot(MediaRoot);
            var root = Rules.Inside(MediaRoot, owner); Directory.CreateDirectory(root);
            var oldLibraries = library.GetVirtualFolders().Select(x => Guid.Parse(x.ItemId)).ToArray();
            var name = "YouTube · " + user.Username;
            var snapshots = access.Users().ToDictionary(u => u.Id, u => users.GetUserDto(u).Policy);
            await library.AddVirtualFolder(name, null, new LibraryOptions
            {
                PathInfos = [new MediaPathInfo { Path = root }], EnableRealtimeMonitor = true,
                TypeOptions = new[] { "Movie", "Video", "Folder", "Series" }.Select(t => new TypeOptions { Type = t, MetadataFetchers = [], ImageFetchers = [] }).ToArray(),
                EnableChapterImageExtraction = false, EnableAutomaticSeriesGrouping = false
            }, true);
            var added = library.GetVirtualFolders().Single(x => x.Name == name);
            try
            {
                foreach (var other in access.Users())
                {
                    var policy = users.GetUserDto(other).Policy;
                    if (policy.IsAdministrator) continue;
                    var allowed = policy.EnableAllFolders ? oldLibraries.ToList() : policy.EnabledFolders.ToList();
                    if (other.Id == user.Id && !allowed.Contains(Guid.Parse(added.ItemId))) allowed.Add(Guid.Parse(added.ItemId));
                    policy.EnableAllFolders = false; policy.EnabledFolders = allowed.ToArray();
                    await users.UpdatePolicyAsync(other.Id, policy);
                }
                var binding = new LibraryBinding(owner, added.ItemId, root);
                config.Libraries.Add(binding); Plugin.Instance.UpdateConfiguration(config);
                return access.Binding(owner);
            }
            catch
            {
                config.Libraries.RemoveAll(b => b.UserId == owner && b.LibraryId == added.ItemId); Plugin.Instance.UpdateConfiguration(config);
                foreach (var snapshot in snapshots) await users.UpdatePolicyAsync(snapshot.Key, snapshot.Value);
                await library.RemoveVirtualFolder(name, true); throw;
            }
        }
        finally { _provision.Release(); }
    }

    public async Task<MediaAsset> Prepare(string owner, VideoInfo video, CancellationToken ct)
    {
        Rules.VideoId(video.Id); var binding = access.Binding(owner);
        if (!Uri.TryCreate(Plugin.Instance.Configuration.PublicBaseUrl, UriKind.Absolute, out var publicUrl)
            || publicUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(publicUrl.UserInfo)
            || !string.IsNullOrEmpty(publicUrl.Query) || !string.IsNullOrEmpty(publicUrl.Fragment))
            throw new InvalidOperationException("Set the externally reachable Jellyfin base URL in JellyTube administrator settings.");
        var key = owner + ":" + video.Id; var gate = _locks.GetOrAdd(key, _ => new(1)); await gate.WaitAsync(ct);
        try
        {
            var asset = store.Get<MediaAsset>(owner, "asset", video.Id);
            if (asset is null)
            {
                var dir = Rules.Inside(binding.Root, video.Id); Directory.CreateDirectory(dir);
                video = video with { Title = video.Title[..Math.Min(video.Title.Length, 500)], Description = video.Description?[..Math.Min(video.Description.Length, 20000)] };
                asset = new(video.Id, owner, video, dir, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
                store.Put(owner, "asset", video.Id, asset);
            }
            var url = Plugin.Instance.Configuration.PublicBaseUrl.TrimEnd('/') + "/JellyTube/Media/" + owner + "/" + video.Id + "/" + asset.Secret + "/manifest.mpd";
            var strm = Path.Combine(asset.Directory, "video.strm");
            if (!File.Exists(strm) || await File.ReadAllTextAsync(strm, ct) != url)
            {
                await File.WriteAllTextAsync(strm + ".tmp", url, ct); File.Move(strm + ".tmp", strm, true);
                var doc = new XDocument(new XElement("movie", new XElement("title", asset.Video.Title),
                    new XElement("plot", asset.Video.Description ?? ""), new XElement("studio", asset.Video.Channel),
                    new XElement("runtime", Math.Max(1, (int)((asset.Video.DurationSeconds ?? 0) / 60))),
                    new XElement("uniqueid", new XAttribute("type", "youtube"), asset.Id),
                    new XElement("thumb", asset.Video.Thumbnail)));
                await File.WriteAllTextAsync(Path.Combine(asset.Directory, "video.nfo"), doc.ToString(), ct);
                await Refresh(owner, ct);
            }
            var item = library.FindByPath(strm, false);
            if (item is not null && item.Id.ToString("N") != asset.ItemId)
            {
                asset = asset with { ItemId = item.Id.ToString("N") }; store.Put(owner, "asset", video.Id, asset);
            }
            return asset;
        }
        finally { gate.Release(); }
    }
    public MediaAsset Resolve(string owner, string id, string secret)
    {
        Rules.VideoId(id); access.Binding(owner);
        var asset = store.Get<MediaAsset>(owner, "asset", id);
        // Each URL is an unguessable revocable capability for exactly one owner/video. It is never a general-purpose proxy.
        if (asset is null || secret.Length != 64 || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(asset.Secret), System.Text.Encoding.ASCII.GetBytes(secret)))
            throw new UnauthorizedAccessException("Invalid media capability.");
        return asset;
    }
    public async Task Refresh(string owner, CancellationToken ct)
    {
        var binding = Plugin.Instance.Configuration.Libraries.FirstOrDefault(b => b.UserId == owner)
            ?? throw new InvalidOperationException("The private library is not configured.");
        var gate = _scans.GetOrAdd(owner, _ => new(1)); await gate.WaitAsync(ct);
        try
        {
            var folder = library.FindByPath(binding.Root, true) as Folder ?? library.GetItemById(Guid.Parse(binding.LibraryId)) as Folder;
            if (folder is null) throw new InvalidOperationException("The native library is not ready. Retry shortly.");
            // Refresh only this owner's YouTube tree. Never rescan the entire NAS for a Play/Favorite action.
            await folder.ValidateChildren(new Progress<double>(), ct);
        }
        finally { gate.Release(); }
    }
    public async Task RewriteUrls(CancellationToken ct)
    {
        foreach (var binding in Plugin.Instance.Configuration.Libraries.ToArray())
            foreach (var asset in store.List<MediaAsset>(binding.UserId, "asset"))
            {
                var gate = _locks.GetOrAdd(binding.UserId + ":" + asset.Id, _ => new(1)); await gate.WaitAsync(ct);
                try
                {
                    var expected = Rules.Inside(binding.Root, Rules.VideoId(asset.Id));
                    if (!Path.GetFullPath(asset.Directory).Equals(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Media directory does not match its library owner.");
                    var path = Path.Combine(expected, "video.strm"); if (!File.Exists(path)) continue;
                    var url = Plugin.Instance.Configuration.PublicBaseUrl.TrimEnd('/') + "/JellyTube/Media/" + binding.UserId + "/" + asset.Id + "/" + asset.Secret + "/manifest.mpd";
                    if (await File.ReadAllTextAsync(path, ct) != url) { await File.WriteAllTextAsync(path + ".tmp", url, ct); File.Move(path + ".tmp", path, true); }
                }
                finally { gate.Release(); }
            }
        foreach (var binding in Plugin.Instance.Configuration.Libraries.ToArray()) await Refresh(binding.UserId, ct);
    }
}
