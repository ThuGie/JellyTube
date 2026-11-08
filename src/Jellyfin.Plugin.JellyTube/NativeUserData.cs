using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Jellyfin user data is the canonical favorite/resume state once a video has a native item.</summary>
public sealed class NativeUserData(Store store, Access access, ILibraryManager library, IUserDataManager userData)
{
    public long PlaybackPosition(string owner, string videoId)
    {
        var asset = store.Get<MediaAsset>(owner, "asset", videoId);
        var direct = store.Get<HistoryEntry>(owner, "resume", videoId);
        var data = Guid.TryParse(asset?.ItemId, out var id) && library.GetItemById(id) is { } item
            ? userData.GetUserData(access.User(owner), item) : null;
        if (direct is not null && (data?.LastPlayedDate is not { } last || direct.WatchedAt.UtcDateTime >= last))
            return TimeSpan.FromSeconds(direct.PositionSeconds).Ticks;
        return data?.PlaybackPositionTicks ?? 0;
    }

    public void SaveProgress(string owner, string videoId, DirectProgressRequest progress, CancellationToken ct)
    {
        var asset = store.Get<MediaAsset>(owner, "direct", videoId)
            ?? throw new KeyNotFoundException("The prepared video is unavailable.");
        ct.ThrowIfCancellationRequested();
        var entry = new HistoryEntry(asset.Video, progress.Completed ? 0 : progress.PositionSeconds, DateTimeOffset.UtcNow);
        store.Put(owner, "resume", videoId, entry);
        if (store.Settings(owner).RecordHistory) store.Put(owner, "history", videoId, entry);
    }

    public Task SetFavorite(string owner, string id, bool favorite, CancellationToken ct)
    {
        var asset = store.Get<MediaAsset>(owner, "asset", id);
        var item = Guid.TryParse(asset?.ItemId, out var itemId) ? library.GetItemById(itemId) : null;
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
        var assets = store.List<MediaAsset>(owner, "asset");
        if (Plugin.Instance.Configuration.SharedLibrary is not null && access.CanUse(owner))
        {
            var known = assets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
            assets.AddRange(store.List<MediaAsset>(NativeLibrary.SharedOwner, "asset").Where(asset => !known.Contains(asset.Id)));
        }
        foreach (var asset in assets)
        {
            var item = Guid.TryParse(asset.ItemId, out var id) ? library.GetItemById(id) : null;
            if (item is null || !item.IsVisible(user)) continue;
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
                var existing = store.Get<HistoryEntry>(owner, "history", asset.Id);
                if ((cleared is null || at > cleared) && (existing is null || at > existing.WatchedAt))
                    store.Put(owner, "history", asset.Id, new HistoryEntry(asset.Video, data.PlaybackPositionTicks / (double)TimeSpan.TicksPerSecond, at));
            }
        }
        return Task.CompletedTask;
    }
}
