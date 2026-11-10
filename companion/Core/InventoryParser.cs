using System.Globalization;
using System.Text.Json;

namespace WarframeCompanion.Core;

public static class InventoryParser
{
    public static readonly string[] CountedKeys =
        ["MiscItems", "Recipes", "RawUpgrades", "Consumables", "FlavourItems", "LevelKeys", "ShipDecorations"];
    public static readonly string[] EquipmentKeys =
        ["Suits", "LongGuns", "Pistols", "Melee", "SpaceSuits", "SpaceGuns", "SpaceMelee",
         "Sentinels", "SentinelWeapons", "KubrowPets", "MoaPets", "Hoverboards", "MechSuits",
         "OperatorAmps", "SpecialItems", "CrewShipWeapons", "CrewShipHarnesses", "KubrowPetEggs"];
    public const int MaxBytes = 32 * 1024 * 1024;

    public static InventorySnapshot Parse(ReadOnlyMemory<byte> bytes, string accountId,
        bool complete = false, DateTimeOffset? readAt = null)
    {
        if (bytes.Length > MaxBytes) throw new InventoryException("Inventory exceeds the 32 MiB limit.");
        ValidateAccount(accountId);
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InventoryException("Inventory must be an object.");
            if (root.TryGetProperty("InventoryJson", out var inner))
            {
                if (inner.ValueKind == JsonValueKind.String)
                {
                    using var nested = JsonDocument.Parse(inner.GetString()!, new JsonDocumentOptions { MaxDepth = 64 });
                    return ParseObject(nested.RootElement, accountId, complete, readAt ?? DateTimeOffset.UtcNow);
                }
                root = inner;
            }
            return ParseObject(root, accountId, complete, readAt ?? DateTimeOffset.UtcNow);
        }
        catch (JsonException) { throw new InventoryException("Inventory is invalid JSON or exceeds the nesting limit."); }
        catch (OverflowException) { throw new InventoryException("An inventory count exceeds the supported integer range."); }
    }

    public static void ValidateAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 128 ||
            accountId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InventoryException("Account key must be 1–128 ASCII letters, digits, underscores or hyphens.");
    }

    private static InventorySnapshot ParseObject(JsonElement root, string account, bool complete, DateTimeOffset read)
    {
        if (root.ValueKind != JsonValueKind.Object || !new[] { "MiscItems", "Suits", "Recipes" }.Any(k => root.TryGetProperty(k, out _)))
            throw new InventoryException("No recognized inventory sections (MiscItems, Suits, Recipes).");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name)) throw new InventoryException("Duplicate inventory section.");
        var identity = Field(root, "AccountId");
        if (identity.ValueKind == JsonValueKind.Object)
        {
            identity = Field(identity, "$oid");
            if (identity.ValueKind != JsonValueKind.String) throw new InventoryException("AccountId must contain a string ObjectId.");
        }
        var verified = identity.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(identity.GetString());
        if (verified && !StringComparer.Ordinal.Equals(identity.GetString(), account))
            throw new InventoryException("Embedded account identity does not match the selected account.");
        if (identity.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
            throw new InventoryException("AccountId has an invalid type.");
        var warnings = new List<string>();
        if (!verified) warnings.Add("Account identity is caller-selected; this payload does not verify ownership.");
        if (!complete) warnings.Add("Partial observation; confirmed inventory will not be replaced.");
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in CountedKeys)
            foreach (var e in Array(root, key))
            {
                var uid = Item(e);
                // Cosmetic records can omit the zero count in the game's inventory serialization.
                var count = Field(e, "ItemCount");
                counts[uid] = checked(counts.GetValueOrDefault(uid) +
                    (key == "FlavourItems" && count.ValueKind == JsonValueKind.Undefined ? 0 : Integer(count, key + ".ItemCount")));
            }
        var equipment = new Dictionary<string, Equipment>(StringComparer.Ordinal);
        foreach (var key in EquipmentKeys)
            foreach (var e in Array(root, key))
            {
                var uid = Item(e);
                var previous = equipment.GetValueOrDefault(uid, new Equipment(0, 0));
                equipment[uid] = new(checked(previous.Count + 1), Math.Max(previous.Xp, OptionalInteger(e, "XP") ?? 0));
            }
        var xp = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in Array(root, "XPInfo"))
        {
            var uid = Item(e);
            xp[uid] = Math.Max(xp.GetValueOrDefault(uid), Integer(Field(e, "XP"), "XPInfo.XP"));
        }
        Dictionary<string, List<long>>? upgrades = null;
        long mods = 0;
        if (root.TryGetProperty("Upgrades", out _))
        {
            upgrades = new(StringComparer.Ordinal);
            foreach (var e in Array(root, "Upgrades"))
            {
                var uid = Item(e);
                var rank = Rank(Field(e, "UpgradeFingerprint"), warnings);
                if (!upgrades.TryGetValue(uid, out var ranks)) upgrades[uid] = ranks = [];
                ranks.Add(rank);
                mods = checked(mods + 1);
            }
        }
        foreach (var e in Array(root, "RawUpgrades")) mods = checked(mods + Integer(Field(e, "ItemCount"), "RawUpgrades.ItemCount"));
        var foundry = Array(root, "PendingRecipes").Select(e => new FoundryJob(Item(e), Date(Field(e, "CompletionDate")))).ToArray();
        Progress? progress = null;
        if (root.TryGetProperty("Missions", out _) || root.TryGetProperty("PlayerSkills", out _))
        {
            var nodes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var m in Array(root, "Missions"))
            {
                var tag = RequiredText(m, "Tag");
                var tier = OptionalInteger(m, "Tier") ?? 0;
                if ((OptionalInteger(m, "Completes") ?? 0) > 0 || tier > 0)
                    nodes[tag] = Math.Max(nodes.GetValueOrDefault(tag), tier);
            }
            var skills = new Dictionary<string, long>(StringComparer.Ordinal);
            var obj = Field(root, "PlayerSkills");
            if (obj.ValueKind != JsonValueKind.Undefined)
            {
                if (obj.ValueKind != JsonValueKind.Object) throw new InventoryException("PlayerSkills must be an object.");
                foreach (var p in obj.EnumerateObject())
                    if (p.Name.StartsWith("LPS_", StringComparison.Ordinal)) skills[p.Name] = Integer(p.Value, "intrinsic");
            }
            progress = new(nodes, skills);
        }
        var syndicates = new Dictionary<string, Affiliation>(StringComparer.Ordinal);
        foreach (var a in Array(root, "Affiliations"))
            syndicates[RequiredText(a, "Tag")] = new(Integer(Field(a, "Standing"), "Standing", signed: true),
                Field(a, "Title").ValueKind == JsonValueKind.Undefined ? 0 : Integer(Field(a, "Title"), "Title", signed: true));
        var daily = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
            if (p.Name.StartsWith("DailyAffiliation", StringComparison.Ordinal)) daily[p.Name] = Integer(p.Value, "daily standing");
        var focus = OptionalInteger(root, "DailyFocus");
        Standing? standing = root.TryGetProperty("Affiliations", out _) || daily.Count > 0 || focus != null ? new(syndicates, daily, focus) : null;
        var timestamp = SyncTime(Field(root, "LastInventorySync"));
        if (timestamp == null) warnings.Add("Source sync timestamp is unavailable; read time is not proof of freshness.");
        return new(1, account, verified, "file-import", read, timestamp, complete, warnings.Distinct().ToArray(),
            counts, equipment, xp, upgrades, foundry,
            new(OptionalInteger(root, "PremiumCredits"), OptionalInteger(root, "RegularCredits"), OptionalInteger(root, "FusionPoints"), OptionalInteger(root, "PrimeTokens")),
            mods, OptionalInteger(root, "PlayerLevel"), progress, standing);
    }

    private static JsonElement Field(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    private static IEnumerable<JsonElement> Array(JsonElement root, string key)
    {
        var value = Field(root, key);
        if (value.ValueKind == JsonValueKind.Undefined) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new InventoryException($"{key} must be an array; existing data was not changed.");
        return value.EnumerateArray();
    }
    private static string Item(JsonElement e) => RequiredText(e, "ItemType");
    private static string RequiredText(JsonElement e, string key)
    {
        var v = Field(e, key);
        if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())) throw new InventoryException($"An entry is missing {key}.");
        return v.GetString()!;
    }
    private static long Integer(JsonElement e, string name, bool signed = false)
    {
        long n;
        var ok = e.ValueKind == JsonValueKind.Number ? e.TryGetInt64(out n) :
            long.TryParse(e.ValueKind == JsonValueKind.String ? e.GetString() : null, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        if (!ok || (!signed && n < 0)) throw new InventoryException($"{name} must be a {(signed ? "signed" : "nonnegative")} integer.");
        return n;
    }
    private static long? OptionalInteger(JsonElement root, string key)
    {
        var e = Field(root, key);
        return e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : Integer(e, key);
    }
    private static long Rank(JsonElement e, List<string> warnings)
    {
        if (e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return 0;
        if (e.ValueKind == JsonValueKind.String)
        {
            try { using var d = JsonDocument.Parse(e.GetString()!); return Rank(d.RootElement, warnings); }
            catch (JsonException) { warnings.Add("Malformed upgrade fingerprint treated as rank zero, matching Rust; rank is unverified."); return 0; }
        }
        if (e.ValueKind != JsonValueKind.Object) throw new InventoryException("Upgrade fingerprint must be an object or encoded object.");
        return OptionalInteger(e, "lvl") ?? 0;
    }
    private static double? Date(JsonElement e)
    {
        if (e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0) return n > 1e11 ? n / 1000 : n;
        var date = Field(e, "$date");
        var number = Field(date, "$numberLong");
        return Integer(number.ValueKind == JsonValueKind.Undefined ? date : number, "CompletionDate") / 1000.0;
    }
    private static double? SyncTime(JsonElement e)
    {
        if (e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n) && n > 0) return n;
        if (e.ValueKind == JsonValueKind.Object) e = Field(e, "$oid");
        if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: 24 } s && s.All(Uri.IsHexDigit) &&
            uint.TryParse(s.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var t)) return t;
        throw new InventoryException("LastInventorySync is not a valid timestamp or ObjectId.");
    }
}
