using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed record ManualCount(long Count, double Time);
public sealed record FarmingTask(string Uid, long? Target, double Added);
public sealed record AccountEvidence(string Id, string Line);
public sealed class ManualState
{
    public Dictionary<string, ManualCount> Counts { get; set; } = [];
    public Dictionary<string, double> Mastered { get; set; } = [];
    public List<string> Pinned { get; set; } = [];
    public List<FarmingTask> Tasks { get; set; } = [];
}
public sealed record Profile(string? Name, string? Id, long? MasteryRank, Dictionary<string, long> Xp, Progress? Progress, Standing? Standing, double Fetched, string Source);
public sealed class AccountState
{
    public required string Key { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Source { get; set; }
    public Profile? Profile { get; set; }
    public ManualState Manual { get; set; } = new();
    public string? ProfileError { get; set; }
    public double? Seen { get; set; }
    public long? Badge { get; set; }
    public long? BadgeMax { get; set; }
    public JsonNode? Duviri { get; set; }
    public List<AccountEvidence> Evidence { get; set; } = [];
}

public sealed class StateStore
{
    private readonly string connectionString;
    public StateStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS documents(key TEXT PRIMARY KEY,value TEXT NOT NULL);"; command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public T? Read<T>(string key)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM documents WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<T>(text, J.Options) : default;
    }
    public void Write<T>(string key, T value)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO documents(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, J.Options)); cmd.ExecuteNonQuery();
    }
    public static Profile ParseProfile(JsonNode data)
    {
        if (data is not JsonObject) throw new InventoryException("Profile must be a JSON object.");
        var root = J.A(data, "Results").FirstOrDefault() ?? data;
        JsonNode? Find(JsonNode? value, string key, int depth = 0)
        {
            if (depth > 6) return null;
            if (J.Get(value, key) is { } direct) return direct;
            return (value is JsonObject o ? o.Select(p => p.Value) : J.A(value).Take(5)).Select(v => Find(v, key, depth + 1)).FirstOrDefault(v => v != null);
        }
        var xp = Find(root, "XPInfo") ?? Find(data, "XPInfo");
        if (xp is not JsonArray) throw new InventoryException("No mastery data (XPInfo) found in the profile JSON.");
        var payload = (JsonObject)root.DeepClone(); payload["MiscItems"] = new JsonArray(); payload["XPInfo"] = xp?.DeepClone();
        var id = J.S(J.Get(J.Get(root, "AccountId"), "$oid")) ?? J.S(root, "AccountId");
        if (id != null) { if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-fA-F0-9]{24}$")) throw new InventoryException("Profile account ID is invalid."); id = id.ToLowerInvariant(); }
        payload.Remove("AccountId");
        var parsed = InventoryParser.Parse(JsonSerializer.SerializeToUtf8Bytes(payload), "profile");
        return new(J.S(Find(root, "DisplayName")), id, J.Get(root, "PlayerLevel") == null && Find(root, "PlayerLevel") == null ? null : J.N(Find(root, "PlayerLevel")),
            parsed.Xp, parsed.Progress, parsed.Standing, J.Now, "Imported profile file");
    }
}
