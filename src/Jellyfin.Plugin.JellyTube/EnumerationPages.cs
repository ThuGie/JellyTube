using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Retains managed provider continuations in bounded, short-lived enumerators.</summary>
public sealed class EnumerationPages : IAsyncDisposable
{
    private sealed class Session
    {
        public readonly CancellationTokenSource Lifetime = new(TimeSpan.FromMinutes(10));
        public required IAsyncEnumerator<VideoInfo> Enumerator;
        public readonly List<VideoInfo> Items = [];
        public DateTimeOffset Accessed = DateTimeOffset.UtcNow;
        public bool Ended;
    }
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly KeyedGate _gates = new();
    public async Task<CatalogPage> Get(string key, int offset, Func<CancellationToken, IAsyncEnumerable<VideoInfo>> create, CancellationToken ct)
    {
        using var gate = await _gates.Enter(key, ct);
        if (_sessions.TryGetValue(key, out var session) && session.Lifetime.IsCancellationRequested)
        {
            _sessions.TryRemove(key, out _); await Close(session); session = null;
        }
        if (session is null)
        {
            session = new() { Enumerator = null! };
            session.Enumerator = create(session.Lifetime.Token).GetAsyncEnumerator(session.Lifetime.Token);
            _sessions[key] = session;
        }
        session.Accessed = DateTimeOffset.UtcNow;
        try
        {
            while (!session.Ended && session.Items.Count < Math.Min(500, offset + 21))
            {
                // Finish the in-flight move before releasing the gate; never overlap MoveNext on cancellation.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var registration = timeout.Token.Register(() => session.Lifetime.Cancel());
                if (await session.Enumerator.MoveNextAsync()) session.Items.Add(session.Enumerator.Current);
                else session.Ended = true;
            }
            return new(session.Items.Skip(offset).Take(20).ToList(), session.Items.Count > offset + 20 && offset + 20 < 500
                ? (offset + 20).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }
        catch { _sessions.TryRemove(key, out _); await Close(session); throw; }
        finally
        {
            foreach (var old in _sessions.Where(pair => pair.Key != key).OrderBy(pair => pair.Value.Accessed).Take(Math.Max(0, _sessions.Count - 32)))
                if (_sessions.TryRemove(old)) _ = Retire(old.Key, old.Value);
        }
    }
    private async Task Retire(string key, Session session)
    {
        using var gate = await _gates.Enter(key, CancellationToken.None);
        await Close(session);
    }
    private static async Task Close(Session session)
    {
        try { session.Lifetime.Cancel(); await session.Enumerator.DisposeAsync(); }
        catch (Exception) { /* Disposal must not hide the provider error. */ }
        finally { session.Lifetime.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _sessions) if (_sessions.TryRemove(pair)) await Retire(pair.Key, pair.Value);
    }
}
