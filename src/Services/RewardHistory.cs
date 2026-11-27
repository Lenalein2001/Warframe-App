using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed class RewardHistory(StateStore store)
{
    private readonly object sync = new();
    public JsonArray Entries { get; private set; } = store.Read<JsonArray>("reward-history") ?? [];
    public void ImportLegacy(string path)
    {
        lock (sync)
        {
            if (store.Read<JsonArray>("reward-history") != null || !File.Exists(path)) return;
            var entries = new JsonArray();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length > 1024 * 1024) continue;
                try
                {
                    var entry = JsonNode.Parse(line);
                    if (J.D(entry?["t"]) is not { } time || time < 0 || entry?["drops"] is not JsonArray drops) continue;
                    var clean = J.A(drops).Where(d => J.S(d, "uid") != null && J.S(d, "name") != null).Select(d => new { name = J.S(d, "name"), uid = J.S(d, "uid"), price = J.D(d["price"]), ducats = J.D(d["ducats"]), needed = J.B(d["needed"]) }).ToArray();
                    if (clean.Length == 0) continue;
                    entries.Add(J.Node(new { t = time, drops = clean, best = J.S(entry, "best"), account = J.S(entry, "account") }));
                    if (entries.Count > 300) entries.RemoveAt(0);
                }
                catch (System.Text.Json.JsonException) { }
                catch (InvalidOperationException) { }
            }
            store.Write("reward-history", entries); Entries = entries;
        }
    }
    public void Record(JsonNode result, string? account)
    {
        var drops = J.A(result, "items").Where(i => J.S(i, "uid") != null).Select(i => new { name = J.S(i, "name"), uid = J.S(i, "uid"), inventoryUid = J.S(i, "inventoryUid"), price = J.Get(i, "price"), ducats = J.Get(i, "ducats"), needed = J.N(i, "needed") > 0 || J.B(J.Get(i, "needed")) }).ToArray();
        if (drops.Length == 0) return;
        lock (sync)
        {
            var next = (JsonArray)Entries.DeepClone();
            var time = J.D(result["t"]) ?? J.Now;
            var screen = J.S(result, "screenId");
            var existing = next.LastOrDefault(e => J.S(e, "account") == account &&
                (screen != null ? J.S(e, "id") == screen : J.D(e?["t"]) == time));
            if (existing == null && screen == null && next.LastOrDefault() is { } last && J.S(last, "account") == account &&
                time - J.D(last["t"]) is >= 0 and < 20 && J.A(last, "drops").Select(x => J.S(x, "uid")).SequenceEqual(drops.Select(d => d.uid))) existing = last;
            var id = existing == null ? screen ?? Guid.NewGuid().ToString("N") : Id(existing);
            var entry = J.Node(new { id, t = J.D(existing?["t"]) ?? time, drops, best = J.S(result, "best"), account });
            if (existing == null) next.Add(entry); else next[next.IndexOf(existing)] = entry;
            while (next.Count > 300) next.RemoveAt(0);
            store.Write("reward-history", next); Entries = next;
        }
    }
    private static string Id(JsonNode row) => J.S(row, "id") ?? "legacy-" + BitConverter.DoubleToInt64Bits(J.D(row["t"]) ?? 0).ToString("X16");

    public JsonNode? Screen(string account, string id)
    {
        lock (sync) return Entries.OfType<JsonNode>().FirstOrDefault(e => J.S(e, "account") == account && Id(e) == id)?.DeepClone();
    }
    public JsonNode Read(string? account, double since)
    {
        lock (sync)
        {
            var rows = Entries.Where(e => J.S(e, "account") == null || J.S(e, "account") == account).ToArray(); var session = rows.Where(e => J.D(e?["t"]) >= since).ToArray();
            var result = J.Node(new { entries = rows.Reverse().Take(40), session = new { since, screens = session.Length, bestValue = session.Sum(e => J.A(e, "drops").Select(d => J.N(d, "price")).DefaultIfEmpty().Max()) } });
            foreach (var row in J.A(result, "entries")) row["id"] = Id(row);
            return result;
        }
    }
}
