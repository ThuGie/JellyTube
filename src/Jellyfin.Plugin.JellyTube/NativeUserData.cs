using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Jellyfin user data is the canonical favorite/resume state once a video has a native item.</summary>
public sealed class NativeUserData(Store store, Access access, ILibraryManager library, IUserDataManager userData)
{
    public Task SetFavorite(string owner, string id, bool favorite, CancellationToken ct)
    {
        var asset = store.Get<MediaAsset>(owner, "asset", id); var item = asset is null ? null : library.FindByPath(Path.Combine(asset.Directory, "video.strm"), false);
        if (item is null) { store.Delete(owner, "favoriteSync", id); return Task.CompletedTask; }
        var user = access.User(owner); var data = userData.GetUserData(user, item) ?? throw new InvalidOperationException("Native user data is not ready yet."); data.IsFavorite = favorite;
        userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, ct);
        store.Put(owner, "favoriteSync", id, true);
        return Task.CompletedTask;
    }
    public Task Synchronize(string owner, CancellationToken ct)
    {
        var user = access.User(owner); var settings = store.Settings(owner);
        var cleared = store.Get<DateTimeOffset?>(owner, "historyClear", "current");
        foreach (var asset in store.List<MediaAsset>(owner, "asset"))
        {
            var item = library.FindByPath(Path.Combine(asset.Directory, "video.strm"), false); if (item is null) continue;
            ct.ThrowIfCancellationRequested(); var data = userData.GetUserData(user, item); if (data is null) continue;
            var saved = store.Get<SavedVideo>(owner, "favorite", asset.Id);
            if (saved is not null && !store.Get<bool>(owner, "favoriteSync", asset.Id))
            {
                data.IsFavorite = true; userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, ct); store.Put(owner, "favoriteSync", asset.Id, true);
            }
            if (data.IsFavorite) store.Put(owner, "favorite", asset.Id, saved ?? new SavedVideo(asset.Video, DateTimeOffset.UtcNow));
            else if (saved is not null) store.Delete(owner, "favorite", asset.Id);
            if (settings.RecordHistory && data.LastPlayedDate is { } last)
            {
                var at = new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc));
                if (cleared is null || at > cleared) store.Put(owner, "history", asset.Id, new HistoryEntry(asset.Video, data.PlaybackPositionTicks / (double)TimeSpan.TicksPerSecond, at));
            }
        }
        return Task.CompletedTask;
    }
}
