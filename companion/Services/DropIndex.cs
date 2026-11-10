using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed record Drop(string Item, string Location, string? Mode, string? Rotation, string? Stage, double Chance, string? Rarity, double? EnemyDropChance, string? EnemyDropKind);
public sealed class DropIndex
{
    private readonly Dictionary<string, List<Drop>> byName = new(StringComparer.OrdinalIgnoreCase);
    public int Available { get; private set; }
    public int Total { get; private set; } = 11;
    public IEnumerable<string> Names => byName.Values.Select(d => d[0].Item);
    public IEnumerable<Drop> ForName(string name, bool blueprint = false) => (byName.GetValueOrDefault(name) ?? [])
        .Concat(blueprint && !name.EndsWith(" Blueprint", StringComparison.OrdinalIgnoreCase) ? byName.GetValueOrDefault(name + " Blueprint") ?? [] : []);
    private void Add(Drop drop) { if (!byName.TryGetValue(drop.Item, out var list)) byName[drop.Item] = list = []; if (!list.Contains(drop)) list.Add(drop); }
    public static DropIndex Build(IReadOnlyDictionary<string, JsonNode> tables, int total)
    {
        var result = new DropIndex { Total = total };
        foreach (var (name, table) in tables)
        {
            if (J.Get(table, name) is not { } data) continue;
            result.Available++;
            if (name is "blueprintLocations" or "modLocations")
            {
                var mods = name == "modLocations";
                foreach (var entry in J.A(data))
                    foreach (var enemy in J.A(entry, "enemies"))
                        if (J.S(entry, mods ? "modName" : "itemName") is { } item && J.S(enemy, "enemyName") is { } location && J.D(J.Get(enemy, "chance")) is { } chance && J.D(J.Get(enemy, mods ? "enemyModDropChance" : "enemyBlueprintDropChance")) is { } drop)
                            result.Add(new(item, location, "Enemy", null, null, chance, J.S(enemy, "rarity"), drop, mods ? "mod" : "blueprint"));
            }
            else result.Walk(data, name == "sortieRewards" ? ["Sortie"] : [], null, null);
        }
        return result;
    }
    private void Walk(JsonNode? value, string[] path, string? mode, string? rotation)
    {
        if (value is JsonArray array) { foreach (var entry in array) Walk(entry, path, mode, rotation); return; }
        if (value is not JsonObject obj) return;
        if (J.S(obj, "itemName") is { } item && J.D(obj["chance"]) is { } chance)
        {
            if (path.Length > 0) Add(new(item, string.Join(" · ", path), mode, J.S(obj, "rotation") ?? rotation, J.S(obj, "stage"), chance, J.S(obj, "rarity"), null, null));
            return;
        }
        var label = J.S(obj, "objectiveName") ?? J.S(obj, "bountyLevel"); var next = label == null ? path : [.. path, label]; mode = J.S(obj, "gameMode") ?? mode;
        foreach (var (key, child) in obj) if (child is JsonArray or JsonObject)
        {
            var isRotation = key is "A" or "B" or "C";
            Walk(child, isRotation || key == "rewards" ? next : [.. next, key], mode, isRotation ? key : rotation);
        }
    }
}
