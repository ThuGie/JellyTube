using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube;

public sealed class Downloads(Store store, Extractor extractor, JellyfinMuxer muxer, NativeLibrary native, Access access, ILogger<Downloads> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private long _retainedBytes, _sharedBytes, _stagingBytes;
    private sealed record Inventory(long TotalBytes, long SharedBytes);
    public sealed record RetainedFile(string RelativePath, long Bytes, int? ActualHeight = null);
    private string FileKey(string path) => Path.GetRelativePath(native.MediaRoot, path).Replace(Path.DirectorySeparatorChar, '/');
    public int? LocalHeight(string id, int height, MediaAsset asset) => PlaybackFile(id, height, asset, height) is { } path
        ? store.Get<RetainedFile>("shared", "retainedFile", FileKey(path))?.ActualHeight : null;
    private void ReconcileStorage()
    {
        var files = Directory.Exists(native.MediaRoot) ? Directory.EnumerateFiles(native.MediaRoot, "*", SearchOption.AllDirectories)
            .Where(x => !x.EndsWith(".nfo") && !x.EndsWith(".strm")).ToArray() : [];
        long total = 0, shared = 0;
        var inventory = new List<(string Id, RetainedFile Value)>();
        var sharedRoot = Path.Combine(native.MediaRoot, ".shared") + Path.DirectorySeparatorChar;
        foreach (var path in files)
        {
            try
            {
                var bytes = new FileInfo(path).Length; total += bytes; if (path.StartsWith(sharedRoot, StringComparison.Ordinal)) shared += bytes;
                var relative = FileKey(path);
                inventory.Add((relative, new(relative, bytes, store.Get<RetainedFile>("shared", "retainedFile", relative)?.ActualHeight)));
            }
            catch (FileNotFoundException) { }
        }
        store.Delete("shared", "retainedFile"); store.PutMany("shared", "retainedFile", inventory);
        Interlocked.Exchange(ref _retainedBytes, total); Interlocked.Exchange(ref _sharedBytes, shared);
        store.Put("shared", "storageInventory", "current", new Inventory(total, shared));
    }
    private void TrackFile(string path, long delta)
    {
        var relative = FileKey(path);
        if (delta < 0) store.Delete("shared", "retainedFile", relative);
        else store.Put("shared", "retainedFile", relative, new RetainedFile(relative, delta));
        Interlocked.Add(ref _retainedBytes, delta);
        if (Path.GetDirectoryName(path) == Path.Combine(native.MediaRoot, ".shared")) Interlocked.Add(ref _sharedBytes, delta);
        store.Put("shared", "storageInventory", "current", new Inventory(Volatile.Read(ref _retainedBytes), Volatile.Read(ref _sharedBytes)));
    }
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
        try { var bytes = File.Exists(path) ? new FileInfo(path).Length : 0; File.Delete(path); TrackFile(path, -bytes); }
        catch (IOException) { logger.LogWarning("A JellyTube media file could not be removed."); }
        catch (UnauthorizedAccessException) { logger.LogWarning("JellyTube has no permission to remove a media file."); }
    }

    private void ReuseLegacy(string id, int height, string final)
    {
        if (File.Exists(final)) return;
        Span<byte> header = stackalloc byte[8];
        foreach (var user in access.Users())
        {
            var owner = Rules.Owner(user.Id);
            var legacy = Rules.Inside(native.MediaRoot, $".retained/{owner}/{id}.mp4");
            MediaAsset? asset;
            lock (_gate)
            {
                var job = store.Get<DownloadJob>(owner, "download", id);
                asset = store.Get<MediaAsset>(owner, "asset", id);
                if (job is not { Status: "Ready" } || job.MaxHeight != height || asset?.LocalFile != legacy || !File.Exists(legacy)) continue;
            }
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
                using (var original = File.OpenRead(legacy))
                using (var replacement = File.OpenRead(temporary))
                    if (original.Length != replacement.Length || !System.Security.Cryptography.SHA256.HashData(original).SequenceEqual(System.Security.Cryptography.SHA256.HashData(replacement)))
                        throw new IOException("Incomplete shared media copy.");
                lock (_gate)
                {
                    if (!File.Exists(final)) { File.Move(temporary, final); TrackFile(final, new FileInfo(final).Length); }
                    store.PutBatch(References(id, legacy).Select(reference =>
                        (reference.Owner, "asset", id, (object)(reference with { LocalFile = final }))));
                    if (!References(id, legacy).Any()) DeleteUnused(legacy);
                }
            }
            finally { File.Delete(temporary); }
            return;
        }
    }
    private DownloadJob[] Matching(DownloadJob job) => access.Users()
        .Select(user => store.Get<DownloadJob>(Rules.Owner(user.Id), "download", job.Id))
        .OfType<DownloadJob>().Where(candidate => candidate.MaxHeight == job.MaxHeight && candidate.Status is "Queued" or "Downloading").ToArray();
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
            var quality = store.Settings(owner).MaxHeight;
            var path = SharedPath(video.Id, quality);
            var available = File.Exists(path);
            var actualHeight = available ? store.Get<RetainedFile>("shared", "retainedFile", FileKey(path))?.ActualHeight : null;
            var job = new DownloadJob(video.Id, owner, video, quality,
                Status: available ? "Ready" : _active.ContainsKey(video.Id + ":" + quality) ? "Downloading" : "Queued",
                UpdatedAt: DateTimeOffset.UtcNow, Progress: available ? 1 : 0, ActualHeight: actualHeight);
            if (available)
            {
                var asset = store.Get<MediaAsset>(owner, "asset", video.Id) ?? new MediaAsset(video.Id, owner, video, "", "");
                store.PutBatch([(owner, "asset", video.Id, asset with { LocalFile = path, Retained = true }),
                    (owner, "download", video.Id, job)]);
                _pendingDeletion.Remove(path);
            }
            else store.Put(owner, "download", video.Id, job);
            if (_wake.CurrentCount == 0) _wake.Release(); return job;
        }
    }
    public void Cancel(string owner, string id)
    {
        lock (_gate)
        {
            var job = store.Get<DownloadJob>(owner, "download", id) ?? throw new KeyNotFoundException();
            if (job.Status is "Ready") throw new InvalidOperationException("A completed retained copy must be removed explicitly.");
            store.Put(owner, "download", id, job with { Status = "Cancelled", UpdatedAt = DateTimeOffset.UtcNow });
            if (_active.TryGetValue(id + ":" + job.MaxHeight, out var token) && Matching(job).Length == 0) token.Cancel();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        ReconcileStorage();
        foreach (var user in access.Users()) foreach (var job in store.List<DownloadJob>(Rules.Owner(user.Id), "download").Where(j => j.Status == "Downloading"))
            store.Put(job.Owner, "download", job.Id, job with { Status = "Queued", Error = "Recovered after restart." });
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = store.NextDownload();
            if (next is null) { await _wake.WaitAsync(stoppingToken); continue; }
            await Run(next, stoppingToken);
        }
    }
    private async Task Run(DownloadJob job, CancellationToken stoppingToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); cancel.CancelAfter(TimeSpan.FromHours(2));
        var key = job.Id + ":" + job.MaxHeight; _active[key] = cancel;
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
                if (Matching(job).Length == 0) return;
                foreach (var subscriber in Matching(job))
                    store.Put(subscriber.Owner, "download", job.Id, subscriber with { Status = "Downloading", Error = null, UpdatedAt = DateTimeOffset.UtcNow });
            }
            if (Directory.Exists(staging))
            {
                var previousBytes = Directory.EnumerateFiles(staging).Sum(path => new FileInfo(path).Length);
                Interlocked.Add(ref _retainedBytes, -previousBytes);
                Interlocked.Exchange(ref _stagingBytes, previousBytes);
            }
            if (!reuse) Directory.CreateDirectory(staging);
            var lastProgress = DateTimeOffset.MinValue; string? lastStage = null;
            var download = reuse ? Task.FromResult(store.Get<RetainedFile>("shared", "retainedFile", FileKey(final))?.ActualHeight ?? 0) : extractor.Download(job.Video, staging, job.MaxHeight, muxer, cancel.Token, (progress, stage) =>
            {
                var now = DateTimeOffset.UtcNow; if (stage == lastStage && (now - lastProgress).TotalMilliseconds < 2000) return;
                lastProgress = now; lastStage = stage;
                lock (_gate)
                {
                    foreach (var current in Matching(job))
                        if (current.Status == "Downloading") store.Put(current.Owner, "download", job.Id, current with { Progress = Math.Clamp(progress, 0, 1), Stage = stage, UpdatedAt = now });
                }
            });
            while (!download.IsCompleted)
            {
                await Task.WhenAny(download, Task.Delay(1000, cancel.Token));
                Interlocked.Exchange(ref _stagingBytes, Directory.Exists(staging) ? Directory.EnumerateFiles(staging).Sum(path => { try { return new FileInfo(path).Length; } catch (FileNotFoundException) { return 0L; } }) : 0);
                if (StorageBytes() > Plugin.Instance.Configuration.StorageBudgetMb * 1024 * 1024) { cancel.Cancel(); try { await download; } catch (OperationCanceledException) { } throw new ProviderException("Download stopped because the storage budget was reached."); }
            }
            var actualHeight = await download;
            access.Binding(job.Owner); cancel.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (Matching(job).Length == 0) throw new OperationCanceledException();
                if (reuse && !File.Exists(final)) throw new ProviderException("The shared copy was removed. Retry the download.");
                if (!reuse)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                    File.Move(Path.Combine(staging, "video.mp4"), final, true);
                    TrackFile(final, new FileInfo(final).Length);
                    Interlocked.Exchange(ref _stagingBytes, 0);
                }
                var updates = new List<(string Owner, string Kind, string Id, object Value)>();
                if (actualHeight > 0)
                {
                    var relative = FileKey(final);
                    updates.Add(("shared", "retainedFile", relative, new RetainedFile(relative, new FileInfo(final).Length, actualHeight)));
                }
                foreach (var subscriber in Matching(job))
                {
                    if (!Plugin.Instance.Configuration.EnableDownloads || Plugin.Instance.Configuration.DownloadDeniedUsers.Contains(subscriber.Owner, StringComparer.OrdinalIgnoreCase)) continue;
                    var asset = store.Get<MediaAsset>(subscriber.Owner, "asset", job.Id) ?? new MediaAsset(job.Id, subscriber.Owner, subscriber.Video, "", "");
                    updates.Add((subscriber.Owner, "asset", job.Id, asset with { LocalFile = final, Retained = true }));
                    updates.Add((subscriber.Owner, "download", job.Id, subscriber with { Status = "Ready", Progress = 1, Stage = "Available locally", UpdatedAt = DateTimeOffset.UtcNow, ActualHeight = actualHeight > 0 ? actualHeight : null }));
                }
                store.PutBatch(updates);
                _pendingDeletion.Remove(final);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                foreach (var current in Matching(job))
                    store.Put(current.Owner, "download", job.Id, current with { Status = stoppingToken.IsCancellationRequested ? "Queued" : "Cancelled", UpdatedAt = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            var message = ex is ProviderException ? ex.Message : "Download could not be completed. Check library access, storage and server diagnostics.";
            lock (_gate)
                foreach (var current in Matching(job))
                    store.Put(current.Owner, "download", job.Id, current with { Status = "Failed", Error = message, UpdatedAt = DateTimeOffset.UtcNow });
            logger.LogWarning("JellyTube download failed: {Reason}", message);
        }
        finally
        {
            _active.TryRemove(key, out _);
            Interlocked.Exchange(ref _stagingBytes, 0);
            // Only the fixed, verified job staging directory is removed; retained/user files are never touched here.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("JellyTube staging cleanup will be retried on the next download; a file is still in use."); }
        }
    }
    public long StorageBytes() => Math.Max(0, Volatile.Read(ref _retainedBytes) + Volatile.Read(ref _stagingBytes));
    public long SharedBytes() => Math.Max(0, Volatile.Read(ref _sharedBytes));
}
