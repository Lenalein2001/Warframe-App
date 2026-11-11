using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WarframeCompanion.Services;

public sealed class CatalogLoader(CachedJson cache)
{
    public const string ExportUrl = "https://raw.githubusercontent.com/calamity-inc/warframe-public-export-plus/senpai/";
    public static readonly string[] InventoryExports = ["ExportResources", "ExportUpgrades", "ExportArcanes", "ExportFlavour", "ExportGear", "ExportKeys", "ExportWeapons", "ExportWarframes", "ExportSentinels", "ExportDrones", "ExportAvionics", "ExportRailjackWeapons"];
    public static readonly string[] DropTables = ["relics", "missionRewards", "transientRewards", "cetusBountyRewards", "solarisBountyRewards", "deimosRewards", "zarimanRewards", "entratiLabRewards", "hexRewards", "sortieRewards", "blueprintLocations", "modLocations"];
    public List<string> Warnings { get; private set; } = [];
    public async Task<Catalog> Load(bool force = false, CancellationToken cancellation = default)
    {
        var raw = new ConcurrentDictionary<string, JsonNode>(); var warnings = new ConcurrentBag<string>();
        async Task Fetch(string name, string url, bool required)
        {
            try
            {
                var result = await cache.Get(url, TimeSpan.FromDays(1), force, cancellation);
                raw[name] = result.Data;
                if (result.Warning != null) warnings.Add(name + ": " + result.Warning);
            }
            catch (Exception e) when (!required && !cancellation.IsCancellationRequested && e is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException)
            { warnings.Add(name + ": unavailable; dependent information is incomplete."); }
        }
        var files = Catalog.MasteryFiles.Concat(["Relics", "Resources", "Gear", "Mods"]).ToArray();
        await Task.WhenAll(files.Select(n => Fetch(n, "https://raw.githubusercontent.com/WFCD/warframe-items/master/data/json/" + n + ".json", n != "Mods"))
            .Concat(InventoryExports.Concat(["ExportRecipes", "ExportRegions", "ExportSyndicates", "dict.en", "ExportRelics", "ExportRewards"]).Select(n => Fetch(n, ExportUrl + n + ".json", false)))
            .Concat(DropTables.Select(n => Fetch("drops_" + n, "https://drops.warframestat.us/data/" + n + ".json", false))));
        var ordered = files.Where(raw.ContainsKey).ToDictionary(n => n, n => raw[n]);
        try
        {
            var compressed = await cache.Blob("https://origin.warframe.com/PublicExport/index_en.txt.lzma", TimeSpan.FromDays(1), force, cancellation);
            var index = System.Text.Encoding.UTF8.GetString(NativeEngine.Unlzma(compressed));
            var recipeFile = index.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ExportRecipes_en.json!", StringComparison.Ordinal)) ?? throw new IOException("Recipe manifest missing.");
            if (recipeFile.Contains('/') || recipeFile.Contains('\\')) throw new IOException("Invalid recipe manifest name.");
            var latest = await cache.Get("https://content.warframe.com/PublicExport/Manifest/" + recipeFile, TimeSpan.FromDays(1), force, cancellation);
            if (latest.Data["ExportRecipes"] is not JsonArray recipeList) throw new IOException("Invalid recipe export schema.");
            var merged = raw.GetValueOrDefault("ExportRecipes") as JsonObject ?? new JsonObject();
            foreach (var recipe in recipeList)
            {
                if (J.S(recipe, "uniqueName") is not { } uid) continue; var entry = merged[uid] as JsonObject;
                if (entry == null) { entry = new JsonObject(); merged[uid] = entry; }
                foreach (var key in new[] { "resultType", "buildPrice", "buildTime", "num", "ingredients" }) if (recipe?[key] is { } value) entry[key] = value.DeepClone();
            }
            raw["ExportRecipes"] = merged;
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        { warnings.Add("Current DE recipe export unavailable; using the cached export-plus recipe data."); }
        if (raw.TryGetValue("ExportRecipes", out var recipes)) ordered["ExportRecipes"] = recipes;
        RepairRelics(raw);
        var cat = Catalog.Build(ordered);
        cat.Dictionary = raw.GetValueOrDefault("dict.en"); cat.Regions = raw.GetValueOrDefault("ExportRegions"); cat.SyndicateExport = raw.GetValueOrDefault("ExportSyndicates");
        foreach (var (tag, node) in J.O(cat.Regions)) if (J.N(node, "masteryExp") > 0) cat.NodeXp[tag] = J.N(node, "masteryExp");
        cat.UpgradeExport = raw.GetValueOrDefault("ExportUpgrades");
        foreach (var file in InventoryExports)
            foreach (var (uid, entry) in J.O(raw.GetValueOrDefault(file)))
            {
                if (J.S(entry, "name") is not { } key) continue;
                var name = J.S(J.Get(cat.Dictionary, key)); var icon = J.S(entry, "icon");
                var kind = file switch { "ExportUpgrades" => "Mods", "ExportArcanes" => "Arcanes", "ExportWeapons" => uid.Contains("/OperatorAmplifiers/", StringComparison.Ordinal) ? "Amps" : "Weapons",
                    "ExportWarframes" => "Warframes", "ExportSentinels" => "Companions", "ExportDrones" or "ExportGear" => "Gear", "ExportAvionics" or "ExportRailjackWeapons" => "Railjack",
                    "ExportFlavour" => uid.Contains("/AvatarImages/", StringComparison.Ordinal) ? "Glyphs" : "Cosmetics", "ExportKeys" => "Quests and keys",
                    _ => uid.Contains("/Fish/", StringComparison.Ordinal) ? "Fish" : uid.Contains("/Gems/", StringComparison.Ordinal) || uid.Contains("/Ores/", StringComparison.Ordinal) ? "Gems and ores" : J.S(entry, "productCategory") == "ShipDecorations" ? "Decorations" : "Resources" };
                var cleaned = name == null ? null : Clean(name);
                cat.Inventory[uid] = new(cleaned, kind, icon?.StartsWith("/Lotus/Interface/", StringComparison.Ordinal) == true ? "https://browse.wf" + icon : null, null,
                    kind is "Mods" or "Arcanes" ? J.N(entry, "fusionLimit") : null, J.S(entry, "rarity"), J.B(J.Get(entry, "excludeFromCodex")));
                if (cleaned != null) cat.Names.TryAdd(uid, cleaned);
            }
        foreach (var (uid, recipe) in cat.Recipes)
        {
            var built = cat.Inventory.GetValueOrDefault(recipe.Result);
            cat.Inventory.TryAdd(uid, new(built?.Name is { } name ? name + " Blueprint" : null, "Blueprints", built?.Icon, recipe.Result, null, null, false));
        }
        cat.LoadedAt = J.Now; Warnings = warnings.Order().ToList();
        var dropData = DropTables.Where(n => n != "relics" && raw.ContainsKey("drops_" + n)).ToDictionary(n => n, n => raw["drops_" + n]);
        cat.Drops = DropIndex.Build(dropData, 11);
        CompactMetadata(cat);
        return cat;
    }
    public static void CompactMetadata(Catalog catalog)
    {
        // Inventory names/ranks were already normalized above. Only randomized
        // mod definitions are needed later, when reading explicit riven data.
        catalog.UpgradeExport = new JsonObject(J.O(catalog.UpgradeExport)
            .Where(p => p.Key.StartsWith("/Lotus/Upgrades/Mods/Randomized/", StringComparison.Ordinal))
            .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (var property in obj) Collect(property.Value);
            else if (node is JsonArray array) foreach (var value in array) Collect(value);
            else if (node is JsonValue value && value.TryGetValue<string>(out var text)) keys.Add(text);
        }
        Collect(catalog.Regions); Collect(catalog.SyndicateExport); Collect(catalog.UpgradeExport);
        catalog.Dictionary = new JsonObject(J.O(catalog.Dictionary).Where(p => keys.Contains(p.Key))
            .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
    }
    public static string Clean(string name)
    {
        name = Regex.Replace(Regex.Replace(name, "<[^>]*>", ""), @"\s+", " ").Trim();
        return name.Any(char.IsLower) ? name : string.Join(' ', name.Split(' ').Select(word => word.Length == 0 ? word : word[..1] + word[1..].ToLowerInvariant()));
    }
    public static void RepairRelics(IReadOnlyDictionary<string, JsonNode> raw)
    {
        if (raw.GetValueOrDefault("Relics") is not JsonArray relics || !relics.All(r => !J.A(r, "rewards").Any())) return;
        var dropNames = J.A(raw.GetValueOrDefault("drops_relics"), "relics").Where(r => J.S(r, "state") == "Intact")
            .GroupBy(r => (J.S(r, "tier") ?? "") + " " + J.S(r, "relicName")).ToDictionary(g => g.Key, g => g.First());
        var dropping = new HashSet<string>();
        void Walk(JsonNode? value)
        {
            if (J.S(value) is { } s && s.EndsWith(" Relic", StringComparison.Ordinal)) dropping.Add(s[..^6]);
            else if (value is JsonObject o) foreach (var p in o) Walk(p.Value);
            else if (value is JsonArray a) foreach (var p in a) Walk(p);
        }
        var bountyTables = DropTables.Skip(1).Take(8).ToArray();
        var fullDrops = bountyTables.All(n => raw.ContainsKey("drops_" + n));
        foreach (var n in bountyTables) Walk(raw.GetValueOrDefault("drops_" + n));
        var byUid = relics.OfType<JsonObject>().Where(r => J.S(r, "uniqueName") != null).ToDictionary(r => J.S(r, "uniqueName")!);
        foreach (var (uid, r) in J.O(raw.GetValueOrDefault("ExportRelics")))
        {
            var refine = J.S(r, "quality") switch { "VPQ_BRONZE" => "Intact", "VPQ_SILVER" => "Exceptional", "VPQ_GOLD" => "Flawless", "VPQ_PLATINUM" => "Radiant", _ => null };
            if (refine == null || J.S(r, "era") is not { } era || J.S(r, "category") is not { } category) continue;
            var name = era + " " + category;
            var rewards = J.A(J.Get(raw.GetValueOrDefault("ExportRewards"), J.S(r, "rewardManifest") ?? "")).SelectMany(J.A).Where(i => J.S(i, "type") != null && J.S(i, "rarity") != null).ToArray();
            if (rewards.Length == 0) continue;
            var names = new Dictionary<string, string>();
            foreach (var rarity in new[] { "COMMON", "UNCOMMON", "RARE" })
            {
                var ids = rewards.Where(i => J.S(i, "rarity") == rarity).Select(i => J.S(i, "type")!.Replace("/StoreItems", "", StringComparison.Ordinal)).ToArray();
                var labels = J.A(dropNames.GetValueOrDefault(name), "rewards").Where(i => (J.D(J.Get(i, "chance")) ?? 0) is var ch && (ch > 20 ? "COMMON" : ch > 5 ? "UNCOMMON" : ch > 0 ? "RARE" : "") == rarity).Select(i => J.S(i, "itemName")).OfType<string>().ToArray();
                HashSet<string> Words(string s) => Regex.Split(Regex.Replace(s.Split('/')[^1].Replace("&", " and "), "([a-z0-9])([A-Z])", "$1 $2").ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0).ToHashSet();
                double Score(string a, string b) { var aa = Words(a); var bb = Words(b); return aa.Intersect(bb).Count() / (double)Math.Max(1, aa.Union(bb).Count()); }
                var taken = new HashSet<string>();
                foreach (var pair in ids.SelectMany(id => labels.Select(label => (id, label, score: Score(id, label)))).OrderByDescending(p => p.score))
                    if (!names.ContainsKey(pair.id) && taken.Add(pair.label)) names[pair.id] = pair.label;
            }
            var rows = rewards.Select(i => {
                var id = J.S(i, "type")!.Replace("/StoreItems", "", StringComparison.Ordinal); var rarity = J.S(i, "rarity")!;
                return new { item = new { uniqueName = id, name = names.GetValueOrDefault(id) ?? Catalog.CamelName(id) }, rarity = Clean(rarity), chance = RelicChance(refine, rarity) }; });
            var vaulted = fullDrops && dropping.Count > 0 ? !dropping.Contains(name) : J.Get(r, "vaultedAt") != null;
            if (!byUid.TryGetValue(uid, out var entry))
            {
                var image = relics.FirstOrDefault(x => J.S(x, "name")?.StartsWith(era + " ", StringComparison.Ordinal) == true)?["imageName"]?.DeepClone();
                entry = new() { ["uniqueName"] = uid, ["name"] = name + " " + refine, ["imageName"] = image }; relics.Add(entry); byUid[uid] = entry;
            }
            entry["rewards"] = J.Array(rows); entry["vaulted"] = vaulted;
        }
    }
    public static double? RelicChance(string refinement, string rarity)
    {
        double[]? row = refinement switch { "Intact" => [25.33, 11, 2], "Exceptional" => [23.33, 13, 4], "Flawless" => [20, 17, 6], "Radiant" => [16.67, 20, 10], _ => null };
        var index = rarity.ToUpperInvariant() switch { "COMMON" => 0, "UNCOMMON" => 1, "RARE" => 2, _ => -1 };
        return row == null || index < 0 ? null : row[index];
    }
}
