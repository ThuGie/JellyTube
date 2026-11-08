using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube;

public sealed class Downloads(Store store, Extractor extractor, JellyfinMuxer muxer, NativeLibrary native, Access access, ILogger<Downloads> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _readers = new();
    private readonly HashSet<string> _pendingDeletion = new();
    private string SharedPath(string id, int height) =>
        Rules.Inside(native.MediaRoot, $".shared/{Rules.VideoId(id)}-{height}.mp4");

    public string? PlaybackFile(string id, int height, MediaAsset? asset, int minimumHeight = 0)
    {
        if (asset?.LocalFile is { } local && File.Exists(local)
            && (minimumHeight == 0 || store.Get<DownloadJob>(asset.Owner.Replace(NativeLibrary.DirectPrefix, "", StringComparison.Ordinal), "download", id)?.MaxHeight >= minimumHeight)) return local;
        var shared = SharedPath(id, height);
        if (File.Exists(shared)) return shared;
        if (asset?.Owner != NativeLibrary.SharedOwner || minimumHeight >= height) return null;
        return new[] { 2160, 1440, 1080, 720, 480, 360 }.Where(quality => quality <= height)
            .Select(quality => SharedPath(id, quality)).FirstOrDefault(File.Exists);
    }

    public Stream? OpenPlayback(string id, int height, MediaAsset asset, Microsoft.AspNetCore.Http.HttpResponse response, int minimumHeight = 0)
    {
        lock (_gate)
        {
            var path = PlaybackFile(id, height, asset, minimumHeight);
            if (path is null || _pendingDeletion.Contains(path)) return null;
            var stream = File.OpenRead(path);
            _readers[path] = _readers.GetValueOrDefault(path) + 1;
            response.OnCompleted(() =>
            {
                stream.Dispose();
                lock (_gate)
                {
                    if (--_readers[path] == 0)
                    {
                        _readers.Remove(path);
                        if (_pendingDeletion.Remove(path)) DeleteUnused(path);
                    }
                }
                return Task.CompletedTask;
            });
            return stream;
        }
    }

    private IEnumerable<MediaAsset> References(string id, string path) =>
        access.Users().Select(user => store.Get<MediaAsset>(Rules.Owner(user.Id), "asset", id))
            .OfType<MediaAsset>().Where(asset => asset.LocalFile == path);

    private void DeleteUnused(string path)
    {
        if (_readers.ContainsKey(path)) { _pendingDeletion.Add(path); return; }
        try { File.Delete(path); }
        catch (IOException) { logger.LogWarning("A JellyTube media file could not be removed."); }
        catch (UnauthorizedAccessException) { logger.LogWarning("JellyTube has no permission to remove a media file."); }
    }

    private void ReuseLegacy(string id, int height, string final)
    {
        lock (_gate)
        {
            if (File.Exists(final)) return;
            Span<byte> header = stackalloc byte[8];
            foreach (var user in access.Users())
            {
                var owner = Rules.Owner(user.Id);
                if (store.Get<DownloadJob>(owner, "download", id) is not { Status: "Ready" } job || job.MaxHeight != height) continue;
                var legacy = Rules.Inside(native.MediaRoot, $".retained/{owner}/{id}.mp4");
                var asset = store.Get<MediaAsset>(owner, "asset", id);
                if (asset?.LocalFile != legacy || !File.Exists(legacy)) continue;
                using (var input = File.OpenRead(legacy))
                {
                    if (input.ReadAtLeast(header, 8, false) != 8 || !header[4..].SequenceEqual("ftyp"u8)) continue;
                    if (input.Length + StorageBytes() > Plugin.Instance.Configuration.StorageBudgetMb * 1024L * 1024) return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                var temporary = final + ".tmp";
                try
                {
                    File.Copy(legacy, temporary, true);
                    if (new FileInfo(temporary).Length != new FileInfo(legacy).Length) throw new IOException("Incomplete shared media copy.");
                    File.Move(temporary, final);
                    store.Put(owner, "asset", id, asset with { LocalFile = final });
                    DeleteUnused(legacy);
                }
                finally { File.Delete(temporary); }
                return;
            }
        }
    }

    public void RemoveRetained(string owner, string id)
    {
        lock (_gate)
        {
            var asset = store.Get<MediaAsset>(owner, "asset", id) ?? throw new KeyNotFoundException();
            var job = store.Get<DownloadJob>(owner, "download", id);
            if (job?.Status is "Queued" or "Downloading") throw new InvalidOperationException("Cancel the active download first.");
            if (asset.LocalFile is { } local)
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var legacy = Rules.Inside(native.MediaRoot, $".retained/{owner}/{id}.mp4");
                var shared = job is null ? null : SharedPath(id, job.MaxHeight);
                var path = Path.GetFullPath(local);
                if (!path.Equals(legacy, comparison) && !path.Equals(shared, comparison))
                    throw new InvalidOperationException("The retained file location is invalid.");
            }
            store.Put(owner, "asset", id, asset with { LocalFile = null, Retained = false });
            store.Delete(owner, "download", id);
            if (asset.LocalFile is { } removed && !References(id, removed).Any()) DeleteUnused(removed);
        }
    }
    public DownloadJob Enqueue(string owner, VideoInfo video)
    {
        access.Binding(owner);
        if (!Plugin.Instance.Configuration.EnableDownloads || Plugin.Instance.Configuration.DownloadDeniedUsers.Contains(owner, StringComparer.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Downloads are disabled for this account.");
        Rules.VideoId(video.Id);
        lock (_gate)
        {
            var previous = store.Get<DownloadJob>(owner, "download", video.Id);
            if (previous is { Status: "Queued" or "Downloading" or "Ready" }) return previous;
            var job = new DownloadJob(video.Id, owner, video, store.Settings(owner).MaxHeight, UpdatedAt: DateTimeOffset.UtcNow);
            store.Put(owner, "download", video.Id, job); if (_wake.CurrentCount == 0) _wake.Release(); return job;
        }
    }
    public void Cancel(string owner, string id)
    {
        lock (_gate)
        {
            var job = store.Get<DownloadJob>(owner, "download", id) ?? throw new KeyNotFoundException();
            if (job.Status is "Ready") throw new InvalidOperationException("A completed retained copy must be removed explicitly.");
            store.Put(owner, "download", id, job with { Status = "Cancelled", UpdatedAt = DateTimeOffset.UtcNow });
            if (_active.TryGetValue(owner + ":" + id, out var token)) token.Cancel();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var user in access.Users()) foreach (var job in store.List<DownloadJob>(Rules.Owner(user.Id), "download").Where(j => j.Status == "Downloading"))
            store.Put(job.Owner, "download", job.Id, job with { Status = "Queued", Error = "Recovered after restart." });
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = access.Users().SelectMany(u => store.List<DownloadJob>(Rules.Owner(u.Id), "download"))
                .Where(j => j.Status == "Queued").OrderBy(j => j.UpdatedAt).FirstOrDefault();
            if (next is null) { await _wake.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken); continue; }
            await Run(next, stoppingToken);
        }
    }
    private async Task Run(DownloadJob job, CancellationToken stoppingToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); cancel.CancelAfter(TimeSpan.FromHours(2));
        var key = job.Owner + ":" + job.Id; _active[key] = cancel;
        var staging = Rules.Inside(native.MediaRoot, ".staging/" + job.Owner + "-" + job.Id);
        try
        {
            access.Binding(job.Owner);
            if (!Plugin.Instance.Configuration.EnableDownloads || Plugin.Instance.Configuration.DownloadDeniedUsers.Contains(job.Owner, StringComparer.OrdinalIgnoreCase)) throw new ProviderException("Downloads have been disabled for this account.");
            var final = SharedPath(job.Id, job.MaxHeight);
            ReuseLegacy(job.Id, job.MaxHeight, final);
            var reuse = File.Exists(final);
            if (!reuse && StorageBytes() >= Plugin.Instance.Configuration.StorageBudgetMb * 1024 * 1024) throw new ProviderException("The storage budget is full.");
            lock (_gate)
            {
                if (store.Get<DownloadJob>(job.Owner, "download", job.Id)?.Status != "Queued") return;
                store.Put(job.Owner, "download", job.Id, job with { Status = "Downloading", Error = null, UpdatedAt = DateTimeOffset.UtcNow });
            }
            if (!reuse) Directory.CreateDirectory(staging);
            var lastProgress = DateTimeOffset.MinValue; string? lastStage = null;
            var download = reuse ? Task.CompletedTask : extractor.Download(job.Video, staging, job.MaxHeight, muxer, cancel.Token, (progress, stage) =>
            {
                var now = DateTimeOffset.UtcNow; if (stage == lastStage && (now - lastProgress).TotalMilliseconds < 500) return;
                lastProgress = now; lastStage = stage;
                lock (_gate)
                {
                    var current = store.Get<DownloadJob>(job.Owner, "download", job.Id);
                    if (current?.Status == "Downloading") store.Put(job.Owner, "download", job.Id, current with { Progress = Math.Clamp(progress, 0, 1), Stage = stage, UpdatedAt = now });
                }
            });
            while (!download.IsCompleted)
            {
                await Task.WhenAny(download, Task.Delay(1000, cancel.Token));
                if (StorageBytes() > Plugin.Instance.Configuration.StorageBudgetMb * 1024 * 1024) { cancel.Cancel(); try { await download; } catch (OperationCanceledException) { } throw new ProviderException("Download stopped because the storage budget was reached."); }
            }
            await download;
            access.Binding(job.Owner); cancel.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (store.Get<DownloadJob>(job.Owner, "download", job.Id)?.Status != "Downloading") throw new OperationCanceledException();
                if (reuse && !File.Exists(final)) throw new ProviderException("The shared copy was removed. Retry the download.");
                if (!reuse)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                    File.Move(Path.Combine(staging, "video.mp4"), final, true);
                }
                var asset = store.Get<MediaAsset>(job.Owner, "asset", job.Id) ?? new MediaAsset(job.Id, job.Owner, job.Video, "", "");
                store.Put(job.Owner, "asset", job.Id, asset with { Owner = job.Owner, LocalFile = final, Retained = true });
                _pendingDeletion.Remove(final);
                store.Put(job.Owner, "download", job.Id, job with { Status = "Ready", Progress = 1, Stage = "Available locally", UpdatedAt = DateTimeOffset.UtcNow });
            }
        }
        catch (OperationCanceledException)
        {
            var current = store.Get<DownloadJob>(job.Owner, "download", job.Id);
            if (current?.Status is not ("Cancelled" or "Ready")) store.Put(job.Owner, "download", job.Id, job with { Status = stoppingToken.IsCancellationRequested ? "Queued" : "Cancelled", UpdatedAt = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            var message = ex is ProviderException ? ex.Message : "Download could not be completed. Check library access, storage and server diagnostics.";
            if (store.Get<DownloadJob>(job.Owner, "download", job.Id)?.Status == "Ready") logger.LogWarning("JellyTube retained media is ready; its library metadata refresh needs a retry.");
            else
            {
                store.Put(job.Owner, "download", job.Id, job with { Status = "Failed", Error = message, UpdatedAt = DateTimeOffset.UtcNow });
                logger.LogWarning("JellyTube download failed: {Reason}", message);
            }
        }
        finally
        {
            _active.TryRemove(key, out _);
            // Only the fixed, verified job staging directory is removed; retained/user files are never touched here.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("JellyTube staging cleanup will be retried on the next download; a file is still in use."); }
        }
    }
    public long StorageBytes() => Directory.Exists(native.MediaRoot)
        ? Directory.EnumerateFiles(native.MediaRoot, "*", SearchOption.AllDirectories).Where(x => !x.EndsWith(".nfo") && !x.EndsWith(".strm")).Sum(x => { try { return new FileInfo(x).Length; } catch (FileNotFoundException) { return 0; } }) : 0;

    public long SharedBytes()
    {
        lock (_gate)
        {
            var directory = Rules.Inside(native.MediaRoot, ".shared");
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.mp4").Sum(path => new FileInfo(path).Length) : 0;
        }
    }
}
