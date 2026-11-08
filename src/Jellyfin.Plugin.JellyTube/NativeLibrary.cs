using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyTube;

public sealed class NativeLibrary(Store store, Access access, ILibraryManager library, IUserManager users, IFileSystem fileSystem, ILibraryMonitor monitor, IUserDataManager userData)
{
    // Newer Jellyfin builds add an optional collection type to this method.
    private static readonly MethodInfo ResolvePathMethod = typeof(ILibraryManager).GetMethods()
        .Where(method => method.Name == nameof(ILibraryManager.ResolvePath))
        .Select(method => (Method: method, Parameters: method.GetParameters()))
        .Where(candidate => candidate.Parameters.Length >= 3
            && candidate.Parameters[0].ParameterType == typeof(FileSystemMetadata)
            && candidate.Parameters[1].ParameterType == typeof(Folder)
            && candidate.Parameters.Skip(2).All(parameter => parameter.IsOptional))
        .OrderBy(candidate => candidate.Parameters.Length)
        .First().Method;
    private readonly SemaphoreSlim _provision = new(1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scans = new();
    public string MediaRoot => string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.MediaRoot)
        ? Path.Combine(Plugin.Instance.DataFolderPath, "media") : Path.GetFullPath(Plugin.Instance.Configuration.MediaRoot);

    public const string SharedOwner = "shared";
    public const string DirectPrefix = "direct-";

    public MediaAsset PrepareDirect(string owner, VideoInfo video)
    {
        var existing = store.Get<MediaAsset>(owner, "direct", video.Id);
        var asset = existing ?? new MediaAsset(video.Id, DirectPrefix + owner, video, "",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        if (existing is null) store.Put(owner, "direct", video.Id, asset);
        return asset;
    }
    public void DisableSharedLibraryWatcher()
    {
        if (Plugin.Instance.Configuration.SharedLibrary is not { } shared
            || !Guid.TryParse(shared.LibraryId, out var id)
            || library.GetItemById(id) is not CollectionFolder folder) return;
        var options = library.GetLibraryOptions(folder);
        if (!options.EnableRealtimeMonitor) return;
        options.EnableRealtimeMonitor = false;
        folder.UpdateLibraryOptions(options);
    }
    public async Task RestoreSharedAccess(IEnumerable<string> owners)
    {
        if (Plugin.Instance.Configuration.SharedLibrary is not { } shared) return;
        var id = Guid.Parse(shared.LibraryId);
        foreach (var owner in owners)
        {
            var user = users.GetUserById(Guid.Parse(owner));
            if (user is null) continue;
            var policy = users.GetUserDto(user).Policy;
            if (policy.EnableAllFolders || policy.EnabledFolders.Contains(id)) continue;
            policy.EnabledFolders = [.. policy.EnabledFolders, id];
            await users.UpdatePolicyAsync(user.Id, policy);
        }
    }
    public async Task<LibraryBinding> Provision(CancellationToken ct)
    {
        await _provision.WaitAsync(ct);
        try
        {
            var config = Plugin.Instance.Configuration;
            if (config.SharedLibrary is null)
            {
                access.CheckMediaRoot(MediaRoot);
                var root = Rules.Inside(MediaRoot, "library");
                Directory.CreateDirectory(root);
                const string name = "JellyTube";
                var added = library.GetVirtualFolders().SingleOrDefault(folder => folder.Name == name);
                if (added is null)
                {
                    await library.AddVirtualFolder(name, null, new LibraryOptions
                    {
                        PathInfos = [new MediaPathInfo { Path = root }], EnableRealtimeMonitor = false,
                        TypeOptions = new[] { "Movie", "Video", "Folder", "Series" }.Select(type => new TypeOptions
                            { Type = type, MetadataFetchers = [], ImageFetchers = [] }).ToArray(),
                        EnableChapterImageExtraction = false, EnableAutomaticSeriesGrouping = false
                    }, true);
                    added = library.GetVirtualFolders().Single(folder => folder.Name == name);
                }
                if (added.Locations is not { Length: 1 } locations || !Rules.PathsOverlap(locations[0], root)
                    || !Path.GetFullPath(locations[0]).Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("A different Jellyfin library is already named JellyTube.");
                config.SharedLibrary = new LibraryBinding(SharedOwner, added.ItemId, root);
                Plugin.Instance.UpdateConfiguration(config);
            }
            var shared = config.SharedLibrary;
            var sharedId = Guid.Parse(shared.LibraryId);
            foreach (var user in access.Users())
            {
                var policy = users.GetUserDto(user).Policy;
                if (policy.IsAdministrator) continue;
                var denied = config.DeniedUsers.Contains(Rules.Owner(user.Id), StringComparer.OrdinalIgnoreCase);
                if (policy.EnableAllFolders && !denied) continue;
                var allowed = policy.EnableAllFolders
                    ? library.GetVirtualFolders().Select(folder => Guid.Parse(folder.ItemId)).ToHashSet()
                    : policy.EnabledFolders.ToHashSet();
                if (denied) allowed.Remove(sharedId);
                else allowed.Add(sharedId);
                policy.EnableAllFolders = false;
                policy.EnabledFolders = allowed.ToArray();
                await users.UpdatePolicyAsync(user.Id, policy);
            }

            foreach (var legacy in config.Libraries.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var user = Guid.TryParse(legacy.UserId, out var id) ? users.GetUserById(id) : null;
                if (user is null || !access.CanUse(legacy.UserId)) continue;
                foreach (var asset in store.List<MediaAsset>(legacy.UserId, "asset"))
                    await Prepare(legacy.UserId, asset.Video, ct);
                var folder = library.GetVirtualFolders().SingleOrDefault(entry => entry.ItemId == legacy.LibraryId);
                if (folder is not null)
                {
                    if (folder.Locations is not { Length: 1 } oldLocations
                        || !Path.GetFullPath(oldLocations[0]).Equals(Path.GetFullPath(legacy.Root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException("A legacy library location changed. Review it in Jellyfin before retiring it.");
                    await library.RemoveVirtualFolder(folder.Name, false);
                }
                config.Libraries.Remove(legacy);
                Plugin.Instance.UpdateConfiguration(config);
            }
            return shared;
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
        var mediaOwner = binding.UserId;
        var key = mediaOwner + ":" + video.Id; var gate = _locks.GetOrAdd(key, _ => new(1)); await gate.WaitAsync(ct);
        string? changingDirectory = null;
        try
        {
            var asset = store.Get<MediaAsset>(mediaOwner, "asset", video.Id);
            if (asset is null || string.IsNullOrEmpty(asset.Directory))
            {
                var dir = Rules.Inside(binding.Root, video.Id);
                monitor.ReportFileSystemChangeBeginning(dir);
                changingDirectory = dir;
                Directory.CreateDirectory(dir);
                video = video with { Title = video.Title[..Math.Min(video.Title.Length, 500)], Description = video.Description?[..Math.Min(video.Description.Length, 20000)] };
                asset = new(video.Id, mediaOwner, video, dir, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                    LocalFile: asset?.LocalFile, Retained: asset?.Retained ?? false);
                store.Put(mediaOwner, "asset", video.Id, asset);
            }
            var url = Plugin.Instance.Configuration.PublicBaseUrl.TrimEnd('/') + "/JellyTube/Media/" + mediaOwner + "/" + video.Id + "/" + asset.Secret + "/manifest.mpd";
            var strm = Path.Combine(asset.Directory, "video.strm");
            if (!File.Exists(strm) || await File.ReadAllTextAsync(strm, ct) != url)
            {
                if (changingDirectory is null)
                {
                    monitor.ReportFileSystemChangeBeginning(asset.Directory);
                    changingDirectory = asset.Directory;
                }
                await File.WriteAllTextAsync(strm + ".tmp", url, ct); File.Move(strm + ".tmp", strm, true);
                var doc = new XDocument(new XElement("movie", new XElement("title", asset.Video.Title),
                    new XElement("plot", asset.Video.Description ?? ""), new XElement("studio", asset.Video.Channel),
                    new XElement("runtime", Math.Max(1, (int)((asset.Video.DurationSeconds ?? 0) / 60))),
                    new XElement("uniqueid", new XAttribute("type", "youtube"), asset.Id),
                    new XElement("thumb", asset.Video.Thumbnail)));
                await File.WriteAllTextAsync(Path.Combine(asset.Directory, "video.nfo"), doc.ToString(), ct);
            }
            var scanGate = _scans.GetOrAdd(mediaOwner, _ => new(1));
            await scanGate.WaitAsync(ct);
            Video item;
            try
            {
                var root = GetOrRegisterFolder(binding.Root, library.RootFolder);
                item = library.FindByPath(strm, false) as Video ?? RegisterVideo(strm, asset.Directory, root, asset.Video);
            }
            finally { scanGate.Release(); }
            if (!item.IsVisible(access.User(owner)))
                throw new UnauthorizedAccessException("Jellyfin did not grant access to this video in the JellyTube library.");
            if (item.Id.ToString("N") != asset.ItemId)
            {
                asset = asset with { ItemId = item.Id.ToString("N") }; store.Put(mediaOwner, "asset", video.Id, asset);
            }
            if (mediaOwner == SharedOwner)
            {
                var personal = store.Get<MediaAsset>(owner, "asset", video.Id);
                MigrateUserData(owner, personal, item, ct);
                // Preserve the account's retained-file reference and use the shared item for sync.
                if (personal?.ItemId != asset.ItemId)
                    store.Put(owner, "asset", video.Id, personal is null ? asset with { Owner = owner }
                        : personal with { ItemId = asset.ItemId, Directory = asset.Directory });
            }
            return asset;
        }
        finally
        {
            if (changingDirectory is not null) monitor.ReportFileSystemChangeComplete(changingDirectory, false);
            gate.Release();
        }
    }

    private Folder GetOrRegisterFolder(string path, Folder parent)
    {
        if (library.FindByPath(path, true) is Folder existing) return existing;
        var folder = new Folder
        {
            Id = library.GetNewItemId(path, typeof(Folder)),
            Path = path,
            Name = Path.GetFileName(path)
        };
        if (library.GetItemById(folder.Id) is Folder registered) return registered;
        parent.AddChild(folder);
        return folder;
    }

    private Video RegisterVideo(string path, string directory, Folder root, VideoInfo info)
    {
        // Some library types resolve a directory containing a single video as the video itself.
        var video = ResolveVideo(fileSystem.GetDirectoryInfo(directory), root);
        var parent = root;
        if (video is null || !string.Equals(video.Path, path, StringComparison.Ordinal))
        {
            parent = GetOrRegisterFolder(directory, root);
            video = ResolveVideo(fileSystem.GetFileInfo(path), parent)
                ?? throw new InvalidOperationException("Jellyfin could not register the YouTube video.");
        }
        if (library.GetItemById(video.Id) is Video registered) return registered;
        video.Name = info.Title;
        video.Overview = info.Description;
        if (info.DurationSeconds is > 0) video.RunTimeTicks = TimeSpan.FromSeconds(info.DurationSeconds.Value).Ticks;
        video.ProviderIds["youtube"] = info.Id;
        parent.AddChild(video);
        return video;
    }

    private Video? ResolveVideo(FileSystemMetadata file, Folder parent)
    {
        var arguments = new object?[ResolvePathMethod.GetParameters().Length];
        Array.Fill(arguments, Type.Missing);
        arguments[0] = file;
        arguments[1] = parent;
        try
        {
            return ResolvePathMethod.Invoke(library, arguments) as Video;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
    private void MigrateUserData(string owner, MediaAsset? previous, Video item, CancellationToken ct)
    {
        if (!Guid.TryParse(previous?.ItemId, out var oldId) || oldId == item.Id || library.GetItemById(oldId) is not { } oldItem) return;
        var user = access.User(owner);
        var oldData = userData.GetUserData(user, oldItem);
        var data = userData.GetUserData(user, item);
        if (oldData is null || data is null) return;
        data.IsFavorite |= oldData.IsFavorite;
        if (data.LastPlayedDate is null || oldData.LastPlayedDate > data.LastPlayedDate)
        {
            data.LastPlayedDate = oldData.LastPlayedDate;
            data.PlaybackPositionTicks = oldData.PlaybackPositionTicks;
            data.Played = oldData.Played;
            data.PlayCount = oldData.PlayCount;
        }
        userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, ct);
    }

    public MediaAsset Resolve(string owner, string id, string secret)
    {
        Rules.VideoId(id);
        var direct = owner.StartsWith(DirectPrefix, StringComparison.Ordinal);
        var account = direct ? owner[DirectPrefix.Length..] : owner;
        if (direct)
        {
            if (!access.CanPlayDirect(account)) throw new UnauthorizedAccessException("Direct playback is unavailable.");
        }
        else if (owner != SharedOwner) access.Binding(owner, legacy: true);
        else if (Plugin.Instance.Configuration.SharedLibrary is null) throw new UnauthorizedAccessException("Shared playback is unavailable.");
        var asset = store.Get<MediaAsset>(account, direct ? "direct" : "asset", id);
        if (asset is null || secret.Length != 64 || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(asset.Secret), System.Text.Encoding.ASCII.GetBytes(secret)))
            throw new UnauthorizedAccessException("Invalid media capability.");
        return direct ? asset with { LocalFile = store.Get<MediaAsset>(account, "asset", id)?.LocalFile } : asset;
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
        foreach (var binding in Plugin.Instance.Configuration.Libraries.Concat(Plugin.Instance.Configuration.SharedLibrary is { } shared ? [shared] : []).ToArray())
            foreach (var asset in store.List<MediaAsset>(binding.UserId, "asset"))
            {
                var gate = _locks.GetOrAdd(binding.UserId + ":" + asset.Id, _ => new(1)); await gate.WaitAsync(ct);
                try
                {
                    var expected = Rules.Inside(binding.Root, Rules.VideoId(asset.Id));
                    if (!Path.GetFullPath(asset.Directory).Equals(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                    var path = Path.Combine(expected, "video.strm"); if (!File.Exists(path)) continue;
                    var url = Plugin.Instance.Configuration.PublicBaseUrl.TrimEnd('/') + "/JellyTube/Media/" + binding.UserId + "/" + asset.Id + "/" + asset.Secret + "/manifest.mpd";
                    if (await File.ReadAllTextAsync(path, ct) != url) { await File.WriteAllTextAsync(path + ".tmp", url, ct); File.Move(path + ".tmp", path, true); }
                }
                finally { gate.Release(); }
            }
        foreach (var binding in Plugin.Instance.Configuration.Libraries.ToArray()) await Refresh(binding.UserId, ct);
    }
}
