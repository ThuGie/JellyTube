using System.Collections.Concurrent;
using System.Diagnostics;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Bounded aggregate measurements. Never stores URLs, account identifiers or capabilities.</summary>
public static class Performance
{
    private sealed class Metric { public long Count, Ticks, Maximum; public readonly Queue<double> Recent = new(); }
    private static readonly ConcurrentDictionary<string, Metric> Metrics = new();
    public static IDisposable Measure(string stage) => new Sample(stage);
    public static void Record(string stage, double milliseconds)
    {
        var metric = Metrics.GetOrAdd(stage, _ => new());
        Interlocked.Increment(ref metric.Count);
        var ticks = (long)(milliseconds * 1000);
        Interlocked.Add(ref metric.Ticks, ticks);
        long previous;
        do { previous = Volatile.Read(ref metric.Maximum); if (previous >= ticks) break; }
        while (Interlocked.CompareExchange(ref metric.Maximum, ticks, previous) != previous);
        lock (metric.Recent) { if (metric.Recent.Count == 512) metric.Recent.Dequeue(); metric.Recent.Enqueue(milliseconds); }
    }
    private static double Percentile(Metric metric)
    {
        lock (metric.Recent)
        {
            var sorted = metric.Recent.Order().ToArray();
            return sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
        }
    }
    public static object Snapshot() => Metrics.OrderBy(pair => pair.Key).Select(pair => new
    {
        stage = pair.Key, count = Volatile.Read(ref pair.Value.Count),
        totalMs = Volatile.Read(ref pair.Value.Ticks) / 1000d,
        maximumMs = Volatile.Read(ref pair.Value.Maximum) / 1000d, p95Ms = Percentile(pair.Value)
    }).ToArray();
    private sealed class Sample(string stage) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => Record(stage, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
    }
}

public sealed class JellyTubeTiming : Microsoft.AspNetCore.Mvc.Filters.ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
    {
        context.ActionDescriptor.RouteValues.TryGetValue("action", out var action);
        var stage = "request." + (action ?? "unknown");
        context.HttpContext.Response.Headers["X-JellyTube-Reference"] = context.HttpContext.TraceIdentifier;
        using var timing = Performance.Measure(stage);
        await next();
    }
}

/// <summary>Exact-key exclusion, removing idle entries without racing queued callers.</summary>
public sealed class KeyedGate
{
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1); public int Users; }
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new();
    public async Task<IDisposable> Enter(string key, CancellationToken ct)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries.Add(key, entry = new());
            entry.Users++;
        }
        try { await entry.Gate.WaitAsync(ct); }
        catch { Release(key, entry, false); throw; }
        return new Lease(() => Release(key, entry, true));
    }
    private void Release(string key, Entry entry, bool acquired)
    {
        lock (_sync)
        {
            if (acquired) entry.Gate.Release();
            if (--entry.Users == 0) { _entries.Remove(key); entry.Gate.Dispose(); }
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
