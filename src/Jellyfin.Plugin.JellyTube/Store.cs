using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>All personal records have an explicit owner in their primary key. No client-selected owner is used by controllers.</summary>
public sealed class Store
{
    private readonly object _gate = new();
    private readonly string _connection;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Store() : this(Path.Combine(Plugin.Instance.DataFolderPath, "jellytube.db")) { }
    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, DefaultTimeout = 5 }.ToString();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS records(owner TEXT NOT NULL,kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,
              PRIMARY KEY(owner,kind,id));
            CREATE TABLE IF NOT EXISTS quotas(day TEXT NOT NULL,owner TEXT NOT NULL,calls INTEGER NOT NULL,PRIMARY KEY(day,owner));
            CREATE TABLE IF NOT EXISTS record_access(owner TEXT NOT NULL,kind TEXT NOT NULL,id TEXT NOT NULL,updated INTEGER NOT NULL,
              PRIMARY KEY(owner,kind,id));
            CREATE INDEX IF NOT EXISTS downloads_status ON records(json_extract(json,'$.status'),json_extract(json,'$.updatedAt')) WHERE kind='download';
            CREATE INDEX IF NOT EXISTS record_access_age ON record_access(owner,kind,updated);
            INSERT OR IGNORE INTO record_access SELECT owner,kind,id,0 FROM records;
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public T? Get<T>(string owner, string kind, string id)
    {
        using var timing = Performance.Measure("db.read");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM records WHERE owner=$owner AND kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
    }
    public List<T> List<T>(string owner, string kind, int limit = int.MaxValue)
    {
        using var timing = Performance.Measure("db.list");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM records WHERE owner=$owner AND kind=$kind ORDER BY id LIMIT $limit";
        cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader(); var items = new List<T>();
        while (reader.Read()) items.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return items;
    }
    public Dictionary<string, T> GetMany<T>(string owner, string kind, IEnumerable<string> ids)
    {
        var keys = ids.Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        if (keys.Length == 0) return result;
        using var timing = Performance.Measure("db.read.batch");
        using var db = Open();
        foreach (var batch in keys.Chunk(50))
        {
            using var cmd = db.CreateCommand();
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind);
            var names = batch.Select((id, index) => { var name = "$id" + index; cmd.Parameters.AddWithValue(name, id); return name; });
            cmd.CommandText = "SELECT id,json FROM records WHERE owner=$owner AND kind=$kind AND id IN (" + string.Join(',', names) + ")";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result[reader.GetString(0)] = JsonSerializer.Deserialize<T>(reader.GetString(1), Json)!;
        }
        return result;
    }
    public int Count(string owner, string kind)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM records WHERE owner=$owner AND kind=$kind";
        cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    public void Put<T>(string owner, string kind, string id, T value) => PutMany(owner, kind, [(id, value)]);
    public void PutMany<T>(string owner, string kind, IEnumerable<(string Id, T Value)> values)
    {
        PutBatch(values.Select(row => (owner, kind, row.Id, (object)row.Value!)));
    }
    public void PutBatch(IEnumerable<(string Owner, string Kind, string Id, object Value)> values)
    {
        var rows = values.Select(row => (row.Owner, row.Kind, row.Id, Json: JsonSerializer.Serialize(row.Value, Json))).ToArray();
        if (rows.Length == 0) return;
        using var timing = Performance.Measure("db.write.batch");
        lock (_gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO records(owner,kind,id,json) VALUES($owner,$kind,$id,$json) ON CONFLICT(owner,kind,id) DO UPDATE SET json=excluded.json WHERE records.json<>excluded.json";
            var ownerParameter = cmd.Parameters.AddWithValue("$owner", ""); var kindParameter = cmd.Parameters.AddWithValue("$kind", "");
            var id = cmd.Parameters.AddWithValue("$id", ""); var json = cmd.Parameters.AddWithValue("$json", "");
            using var stamp = db.CreateCommand(); stamp.Transaction = tx;
            stamp.CommandText = "INSERT INTO record_access VALUES($owner,$kind,$id,$time) ON CONFLICT(owner,kind,id) DO UPDATE SET updated=excluded.updated";
            var stampOwner = stamp.Parameters.AddWithValue("$owner", ""); var stampKind = stamp.Parameters.AddWithValue("$kind", "");
            var stampId = stamp.Parameters.AddWithValue("$id", ""); stamp.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            foreach (var row in rows)
            {
                ownerParameter.Value = row.Owner; kindParameter.Value = row.Kind;
                id.Value = row.Id; json.Value = row.Json;
                if (cmd.ExecuteNonQuery() == 0) continue;
                stampOwner.Value = row.Owner; stampKind.Value = row.Kind; stampId.Value = row.Id; stamp.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    public void Trim(string owner, string kind, int keep)
    {
        lock (_gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM records WHERE owner=$owner AND kind=$kind AND id IN (SELECT id FROM record_access WHERE owner=$owner AND kind=$kind ORDER BY updated DESC,id LIMIT -1 OFFSET $keep); DELETE FROM record_access WHERE owner=$owner AND kind=$kind AND NOT EXISTS(SELECT 1 FROM records r WHERE r.owner=record_access.owner AND r.kind=record_access.kind AND r.id=record_access.id)";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$keep", keep);
            cmd.ExecuteNonQuery(); tx.Commit();
        }
    }
    public void Delete(string owner, string kind, string? id = null)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM records WHERE owner=$owner AND kind=$kind" + (id is null ? "" : " AND id=$id");
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind);
            if (id is not null) cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM record_access WHERE owner=$owner AND kind=$kind" + (id is null ? "" : " AND id=$id");
            cmd.ExecuteNonQuery();
        }
    }
    public PersonalPlaylist SavePlaylist(string owner, PersonalPlaylist value)
    {
        lock (_gate)
        {
            var current = Get<PersonalPlaylist>(owner, "playlist", value.Id);
            if (current is null || current.Version != value.Version) throw new InvalidOperationException("Playlist changed. Reload before saving.");
            if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 120 || value.Videos.Count > 1000) throw new ArgumentException("Invalid playlist.");
            foreach (var v in value.Videos) Rules.VideoId(v.Id);
            value = value with { Version = current.Version + 1 };
            Put(owner, "playlist", value.Id, value); return value;
        }
    }
    public bool ClaimSearch(string owner, int serverLimit, int userLimit, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            // YouTube's daily quotas reset at midnight Pacific, including daylight saving time.
            var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");
            var day = TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd");
            cmd.CommandText = "SELECT coalesce(sum(calls),0),coalesce(sum(CASE WHEN owner=$owner THEN calls ELSE 0 END),0) FROM quotas WHERE day=$day";
            cmd.Parameters.AddWithValue("$day", day); cmd.Parameters.AddWithValue("$owner", owner);
            using (var reader = cmd.ExecuteReader())
            {
                reader.Read(); if (reader.GetInt64(0) >= serverLimit || reader.GetInt64(1) >= userLimit) return false;
            }
            cmd.CommandText = "INSERT INTO quotas VALUES($day,$owner,1) ON CONFLICT(day,owner) DO UPDATE SET calls=calls+1";
            cmd.ExecuteNonQuery(); tx.Commit(); return true;
        }
    }
    public DownloadJob? NextDownload()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM records WHERE kind='download' AND json_extract(json,'$.status')='Queued' ORDER BY json_extract(json,'$.updatedAt') LIMIT 1";
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<DownloadJob>(json, Json) : null;
    }
    public sealed record PublicVideo(DateTimeOffset CreatedAt, VideoInfo Video);
    public VideoInfo? CachedVideo(string owner, string id)
    {
        if (Get<string>(owner, "videoRef", id) is not null)
            return Get<PublicVideo>("public", "metadata", id) is { } entry && entry.CreatedAt.AddHours(6) > DateTimeOffset.UtcNow ? entry.Video : null;
        var legacy = Get<VideoInfo>(owner, "video", id);
        if (legacy is not null) { CacheVideos(owner, [legacy]); Delete(owner, "video", id); }
        return legacy;
    }
    public List<VideoInfo> CacheVideos(string owner, IEnumerable<VideoInfo> videos)
    {
        var values = videos.ToArray();
        var changed = new List<(string Id, PublicVideo Value)>();
        var references = new List<(string Id, string Value)>();
        var canonical = GetMany<PublicVideo>("public", "metadata", values.Select(video => video.Id));
        var known = GetMany<string>(owner, "videoRef", values.Select(video => video.Id));
        var output = new List<VideoInfo>(values.Length);
        foreach (var video in values)
        {
            canonical.TryGetValue(video.Id, out var existing);
            var merged = EnrichVideo(video, existing);
            output.Add(merged);
            if (existing is null || existing.CreatedAt.AddHours(6) <= DateTimeOffset.UtcNow
                || JsonSerializer.Serialize(existing.Video, Json) != JsonSerializer.Serialize(merged, Json))
                changed.Add((video.Id, new(DateTimeOffset.UtcNow, merged)));
            if (!known.ContainsKey(video.Id)) references.Add((video.Id, video.Id));
        }
        if (changed.Count > 0) { PutMany("public", "metadata", changed); Trim("public", "metadata", 5000); }
        if (references.Count > 0) { PutMany(owner, "videoRef", references); Trim(owner, "videoRef", 1000); }
        return output;
    }
    public List<VideoInfo> EnrichVideos(IEnumerable<VideoInfo> videos)
    {
        var values = videos.ToList();
        var cached = GetMany<PublicVideo>("public", "metadata", values.Where(video => video.Tags is null).Select(video => video.Id));
        return values.Select(video => EnrichVideo(video, cached.GetValueOrDefault(video.Id))).ToList();
    }
    private static VideoInfo EnrichVideo(VideoInfo video, PublicVideo? cached)
    {
        if (cached is null || cached.CreatedAt.AddHours(6) <= DateTimeOffset.UtcNow) return video;
        return video with
        {
            PublishedAt = video.PublishedAt ?? cached.Video.PublishedAt,
            DurationSeconds = video.DurationSeconds ?? cached.Video.DurationSeconds,
            Description = video.Tags is null ? cached.Video.Description ?? video.Description : video.Description,
            Tags = video.Tags ?? cached.Video.Tags, IsShort = video.IsShort ?? cached.Video.IsShort
        };
    }
    public UserSettings Settings(string owner) => Get<UserSettings>(owner, "settings", "current") ?? new();
}
