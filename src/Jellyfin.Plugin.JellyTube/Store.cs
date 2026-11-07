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
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS records(owner TEXT NOT NULL,kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,
              PRIMARY KEY(owner,kind,id));
            CREATE TABLE IF NOT EXISTS quotas(day TEXT NOT NULL,owner TEXT NOT NULL,calls INTEGER NOT NULL,PRIMARY KEY(day,owner));
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public T? Get<T>(string owner, string kind, string id)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT json FROM records WHERE owner=$owner AND kind=$kind AND id=$id";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
        }
    }
    public List<T> List<T>(string owner, string kind)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT json FROM records WHERE owner=$owner AND kind=$kind ORDER BY id";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind);
            using var reader = cmd.ExecuteReader(); var items = new List<T>();
            while (reader.Read()) items.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
            return items;
        }
    }
    public void Put<T>(string owner, string kind, string id, T value)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO records VALUES($owner,$kind,$id,$json) ON CONFLICT(owner,kind,id) DO UPDATE SET json=excluded.json";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json)); cmd.ExecuteNonQuery();
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
    public UserSettings Settings(string owner) => Get<UserSettings>(owner, "settings", "current") ?? new();
}
