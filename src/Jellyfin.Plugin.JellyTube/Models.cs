using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTube;

public sealed class Configuration : BasePluginConfiguration
{
    public string ApiKey { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public string MediaRoot { get; set; } = "";
    public long StorageBudgetMb { get; set; } = 20480;
    public int SearchCallsPerDay { get; set; } = 90;
    public int UserSearchCallsPerDay { get; set; } = 20;
    public bool EnableDownloads { get; set; } = true;
    public string[] DeniedUsers { get; set; } = [];
    public string[] DownloadDeniedUsers { get; set; } = [];
    public string MinimumSafeSearch { get; set; } = "moderate";
    public List<LibraryBinding> Libraries { get; set; } = [];
}

public sealed class LibraryBinding
{
    public string UserId { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string Root { get; set; } = "";
    public LibraryBinding() { }
    public LibraryBinding(string userId, string libraryId, string root) { UserId = userId; LibraryId = libraryId; Root = root; }
}
public sealed record VideoInfo(string Id, string Title, string Channel, string ChannelId, string Thumbnail,
    double? DurationSeconds = null, string? Description = null, DateTimeOffset? PublishedAt = null, string[]? Tags = null);
public sealed record CatalogPage(List<VideoInfo> Items, string? NextPageToken = null);
public sealed record UserSettings(bool Autoplay = false, int MaxHeight = 720, string Language = "en",
    string Region = "NL", string SafeSearch = "strict", bool RecordHistory = true, string View = "grid");
public sealed record SavedVideo(VideoInfo Video, DateTimeOffset AddedAt);
public sealed record ChannelInfo(string Id, string Name, string Thumbnail);
public sealed record SavedChannel(ChannelInfo Channel, DateTimeOffset AddedAt);
public sealed record DiscoveryResult(string Kind, string Id, string Title, string Author);
public sealed record PersonalPlaylist(string Id, string Name, List<VideoInfo> Videos, long Version = 1);
public sealed record HistoryEntry(VideoInfo Video, double PositionSeconds, DateTimeOffset WatchedAt);
public sealed record MediaAsset(string Id, string Owner, VideoInfo Video, string Directory, string Secret,
    string? ItemId = null, string? LocalFile = null, bool Retained = false);
public sealed record DownloadJob(string Id, string Owner, VideoInfo Video, int MaxHeight, string Status = "Queued",
    string? Error = null, DateTimeOffset? UpdatedAt = null, double Progress = 0, string? Stage = null);
public sealed record ExtractedSource(string Url, Dictionary<string, string> Headers, string Extension,
    string VideoCodec, string AudioCodec, int Width, int Height, double Duration, DateTimeOffset ExpiresAt, long Size = 0);
public sealed record AdaptiveTrack(string Url, string Codec, long Size, long Bitrate, int Width, int Height,
    long IndexStart, long IndexEnd, long InitEnd, uint Timescale = 1, ulong PresentationTime = 0, List<MediaSegment>? Segments = null, string IndexIdentity = "");
public sealed record MediaSegment(long Start, long End, uint Duration);
public sealed record SegmentIndex(long Start, long End, long InitEnd, uint Timescale, ulong PresentationTime, List<MediaSegment> Segments);
public sealed record AdaptiveSource(AdaptiveTrack Video, AdaptiveTrack Audio, DateTimeOffset ExpiresAt);

public static partial class Rules
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoPattern();
    public static string VideoId(string id) => VideoPattern().IsMatch(id) ? id : throw new ValidationException("Invalid YouTube video ID.");
    public static string Owner(Guid id) => id == Guid.Empty ? throw new UnauthorizedAccessException() : id.ToString("N");
    public static UserSettings Settings(UserSettings value)
    {
        if (value.MaxHeight is not (360 or 480 or 720 or 1080 or 1440 or 2160)
            || value.SafeSearch is not ("strict" or "moderate") || value.View is not ("grid" or "list")
            || !Regex.IsMatch(value.Region, "^[A-Z]{2}$") || !Regex.IsMatch(value.Language, "^[a-z]{2,3}(-[A-Za-z]{2,4})?$"))
            throw new ValidationException("Invalid personal settings.");
        return value;
    }
    public static string Inside(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ValidationException("Path escapes the configured directory.");
        return result;
    }
    public static bool PathsOverlap(string first, string second)
    {
        var a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return a.Equals(b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison) || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }
}
