using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyTube;

public sealed class Access(IUserManager users, ILibraryManager library)
{
    public User User(string owner)
    {
        if (!Guid.TryParse(owner, out var id) || id == Guid.Empty) throw new UnauthorizedAccessException("A Jellyfin user session is required.");
        var user = users.GetUserById(id) ?? throw new UnauthorizedAccessException("This Jellyfin account no longer exists.");
        var policy = users.GetUserDto(user).Policy;
        if (policy.IsDisabled || (!policy.IsAdministrator && Plugin.Instance.Configuration.DeniedUsers.Contains(id.ToString("N"), StringComparer.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("JellyTube access is disabled for this account.");
        return user;
    }
    public bool IsAdmin(string owner) => users.GetUserDto(User(owner)).Policy.IsAdministrator;
    public bool CanPlayDirect(string owner)
    {
        var policy = users.GetUserDto(User(owner)).Policy;
        if (!policy.EnableMediaPlayback) throw new UnauthorizedAccessException("Media playback is disabled for this Jellyfin account.");
        return !policy.ForceRemoteSourceTranscoding;
    }
    public bool Administrator(User user) => users.GetUserDto(user).Policy.IsAdministrator;
    public IEnumerable<User> Users()
    {
        // 10.11.11 replaced IUserManager.Users with GetUsers without changing the assembly version.
        var type = typeof(IUserManager);
        return ((System.Collections.IEnumerable?)(type.GetMethod("GetUsers", Type.EmptyTypes)?.Invoke(users, null)
            ?? type.GetProperty("Users")?.GetValue(users)))?.Cast<User>() ?? [];
    }
    public LibraryBinding Binding(string owner, bool legacy = false)
    {
        var user = User(owner);
        var config = Plugin.Instance.Configuration;
        var binding = (legacy ? null : config.SharedLibrary) ?? config.Libraries.FirstOrDefault(x => x.UserId == owner)
            ?? throw new InvalidOperationException("An administrator needs to set up the shared JellyTube library.");
        if (library.GetVirtualFolders().Where(f => !string.Equals(f.ItemId, binding.LibraryId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.Locations ?? []).Any(path => !string.IsNullOrWhiteSpace(path) && Rules.PathsOverlap(binding.Root, path)))
            throw new UnauthorizedAccessException("The JellyTube folder overlaps another library. Ask an administrator to choose separate library locations.");
        var folder = Guid.TryParse(binding.LibraryId, out var id) ? library.GetItemById(id) : null;
        if (folder is null || !folder.IsVisible(user)) throw new UnauthorizedAccessException("Your JellyTube library is unavailable.");
        // Fail closed if a later Jellyfin policy change accidentally exposes the library to another non-administrator.
        if (binding != config.SharedLibrary && Users().Any(other => other.Id != user.Id && !users.GetUserDto(other).Policy.IsAdministrator
                && !users.GetUserDto(other).Policy.IsDisabled && folder.IsVisible(other)))
            throw new UnauthorizedAccessException("This JellyTube library is visible to another account. Ask an administrator to correct library permissions.");
        return binding;
    }
    public bool CanUse(string owner)
    {
        try { Binding(owner); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException) { return false; }
    }
    private static IEnumerable<LibraryBinding> ManagedLibraries() => Plugin.Instance.Configuration.Libraries
        .Concat(Plugin.Instance.Configuration.SharedLibrary is { } shared ? [shared] : []);
    public void CheckMediaRoot(string root)
    {
        var privateIds = ManagedLibraries().Select(b => b.LibraryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (library.GetVirtualFolders().Where(f => !privateIds.Contains(f.ItemId)).SelectMany(f => f.Locations ?? []).Any(path => Rules.PathsOverlap(root, path)))
            throw new System.ComponentModel.DataAnnotations.ValidationException("Choose a dedicated JellyTube directory outside all existing shared library paths.");
    }
    public bool OverlapsPrivateLibrary(string path) => ManagedLibraries().Any(b => Rules.PathsOverlap(path, b.Root));
    public bool UnchangedLibraryRoot(Guid libraryId, string path) => ManagedLibraries().Any(b => Guid.TryParse(b.LibraryId, out var id) && id == libraryId
        && Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(Path.GetFullPath(b.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
}
