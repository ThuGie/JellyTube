namespace Jellyfin.Plugin.JellyTube;

/// <summary>Shares work while callers remain interested; the last departing caller cancels the worker.</summary>
public sealed class SharedWork<T>
{
    private sealed class Entry
    {
        public readonly CancellationTokenSource Cancellation = new();
        public required Lazy<Task<T>> Work;
        public int Callers;
        public bool Completed;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();
    public int Count { get { lock (_gate) return _entries.Count; } }
    public async Task<T> Run(string key, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new() { Work = null! };
                var current = entry;
                entry.Work = new(() => Execute(work, current));
                _entries.Add(key, entry);
            }
            entry.Callers++;
        }
        try
        {
            var task = entry.Work.Value;
            _ = task.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await task.WaitAsync(ct);
        }
        finally
        {
            bool cancel = false;
            lock (_gate)
            {
                if (--entry.Callers == 0)
                {
                    if (_entries.TryGetValue(key, out var current) && ReferenceEquals(entry, current)) _entries.Remove(key);
                    cancel = !entry.Completed;
                    if (entry.Completed) entry.Cancellation.Dispose();
                }
            }
            if (cancel) { try { entry.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* Worker completed during detach. */ } }
        }
    }
    private async Task<T> Execute(Func<CancellationToken, Task<T>> work, Entry entry)
    {
        try { return await work(entry.Cancellation.Token); }
        finally
        {
            lock (_gate)
            {
                entry.Completed = true;
                if (entry.Callers == 0) entry.Cancellation.Dispose();
            }
        }
    }
}
