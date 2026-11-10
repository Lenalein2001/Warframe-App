using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WarframeCompanion.Services;

public sealed record Component(string Uid, long Count, string Kind, string Name);
public sealed record CraftUse(string Uid, string Name, long Count);
public sealed class GameItem
{
    public required string Uid { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Kind { get; init; }
    public bool IsPrime { get; init; }
    public bool Vaulted { get; init; }
    public long MasteryReq { get; init; }
    public long MaxRank { get; init; } = 30;
    public double XpFactor { get; init; } = 1000;
    public string? Image { get; init; }
    public double? Disposition { get; init; }
    public long? DispoDots { get; init; }
    public string RivenClass { get; init; } = "rifle";
    public List<Component> Components { get; set; } = [];
    public string? SetSlug => IsPrime ? Catalog.Slugify(Name + " Set") : null;
}
public sealed record PartRelic(string Relic, string? Rarity, double? Chance, bool Vaulted);
public sealed class GamePart(string uid, string name, string? slug, string? alt)
{
    public string Uid { get; } = uid;
    public string Name { get; } = name;
    public string? Slug { get; } = slug;
    public string? Alt { get; } = alt;
    public List<string> Items { get; } = [];
    public List<PartRelic> Relics { get; } = [];
    public bool Vaulted => Relics.Count > 0 && Relics.All(r => r.Vaulted);
}
public sealed class RelicReward(string uid, string name, string? slug, string? rarity)
{
    public string Uid { get; set; } = uid;
    public string Name { get; } = name;
    public string? Slug { get; } = slug;
    public string? Rarity { get; set; } = rarity;
    public Dictionary<string, double?> Chance { get; } = [];
}
public sealed class GameRelic(string name)
{
    public string Name { get; } = name;
    public string Era => Name.Split(' ')[0];
    public bool Vaulted { get; set; } = true;
    public string? Slug { get; set; }
    public string? Image { get; set; }
    public Dictionary<string, string> Uids { get; } = [];
    public Dictionary<string, RelicReward> Rewards { get; } = [];
}
public sealed record CraftNeed(string Uid, string Name, long Count, string? Image = null);
public sealed record PartRecipe(string Result, long Quantity, long? Credits, long? Seconds, List<CraftNeed> Ingredients);
public sealed record Craft(string Uid, string Name, string Kind, string? Image, long MasteryReq, long Quantity, List<CraftNeed> Needs);
public sealed record LookupItem(string Uid, string Name, string Category, string? Image);
public sealed record InventoryEntry(string? Name, string Kind, string? Icon, string? Result, long? Rank, string? Rarity, bool Hidden);

public sealed class Catalog
{
    public const string Plexus = "/Lotus/Types/Game/CrewShip/RailJack/DefaultHarness";
    public static readonly string[] MasteryFiles = ["Warframes", "Primary", "Secondary", "Melee", "Archwing", "Arch-Gun", "Arch-Melee", "Sentinels", "SentinelWeapons", "Pets", "Misc"];
    public static readonly string[] Refinements = ["Intact", "Exceptional", "Flawless", "Radiant"];
    public Dictionary<string, GameItem> Items { get; } = [];
    public Dictionary<string, GamePart> Parts { get; } = [];
    public Dictionary<string, string> Aliases { get; } = [];
    public Dictionary<string, GameRelic> Relics { get; } = [];
    public Dictionary<string, (string Name, string Refinement)> RelicUids { get; } = [];
    public Dictionary<string, string> Names { get; } = [];
    public Dictionary<string, string> Art { get; } = [];
    public Dictionary<string, LookupItem> Lookup { get; } = [];
    public Dictionary<string, PartRecipe> Recipes { get; } = [];
    public Dictionary<string, CraftUse[]> CraftingUses { get; } = [];
    public Dictionary<string, InventoryEntry> Inventory { get; } = [];
    public List<Craft> Crafts { get; } = [];
    public Dictionary<string, string> Resources { get; } = [];
    public Dictionary<string, long> NodeXp { get; } = [];
    public JsonNode? Regions { get; set; }
    public JsonNode? SyndicateExport { get; set; }
    public JsonNode? UpgradeExport { get; set; }
    public JsonNode? Dictionary { get; set; }
    public double? LoadedAt { get; set; }
    public DropIndex Drops { get; set; } = new();
    public bool RecipesAvailable => Recipes.Count > 0;
    public string NameOf(string uid) => Items.TryGetValue(uid, out var it) ? it.Name : Parts.TryGetValue(uid, out var p) ? p.Name :
        RelicUids.TryGetValue(uid, out var r) ? $"{r.Name} {r.Refinement}" : Names.GetValueOrDefault(uid) ?? CamelName(uid);
    public string? ImageFor(string uid) => ImageUrl(Items.GetValueOrDefault(uid)?.Image) ?? Inventory.GetValueOrDefault(uid)?.Icon;
    public static string? ImageUrl(string? image) => image == null ? null : image.StartsWith("https://", StringComparison.Ordinal) ? image : "https://cdn.warframestat.us/img/" + image;
    public static string ArtKey(string name) => string.Concat(Regex.Split(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w != "and"));
    public string? ArtByName(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && long.TryParse(words[0].TrimEnd('x', 'X'), out _)) words.RemoveAt(0);
        while (words.Count > 0)
        {
            if (Art.TryGetValue(ArtKey(string.Join(' ', words)), out var image)) return ImageUrl(image);
            words.RemoveAt(words.Count - 1);
        }
        return null;
    }
    public static string Slugify(string name) => Regex.Replace(name.ToLowerInvariant().Replace("&", "and"), "[^a-z0-9]+", "_").Trim('_');
    public static string CamelName(string uid)
    {
        var leaf = uid.Split('/')[^1];
        leaf = Regex.Replace(leaf, "(Blueprint|Component)$", "");
        var words = Regex.Replace(leaf, "([a-z0-9])([A-Z])", "$1 $2");
        return uid.Contains("/WarframeRecipes/", StringComparison.Ordinal) ? Regex.Replace(words, @"\bHelmet$", "Neuroptics") : words;
    }
    public static string? AltUid(string uid) => uid.EndsWith("Component", StringComparison.Ordinal) ? uid[..^9] + "Blueprint" :
        uid.EndsWith("Blueprint", StringComparison.Ordinal) && uid.Contains("/WarframeRecipes/", StringComparison.Ordinal) ? uid[..^9] + "Component" : null;
    public static (string Base, string Slot)? SplitSlot(string stem)
    {
        var lower = stem.ToLowerInvariant();
        if (lower.EndsWith("item", StringComparison.Ordinal)) lower = lower[..^4];
        foreach (var slot in new[] { "neuroptics", "helmet", "chassis", "systems", "receiver", "barrel", "stock", "blade", "handle", "hilt", "string", "grip", "limb", "link", "gauntlet", "glove", "guard", "head", "disc", "pouch", "stars", "carapace", "cerebrum", "harness", "wings", "heatsink", "motor", "engine", "ornament", "aegis", "rivet", "hook", "chain", "core" })
            if (lower.EndsWith(slot, StringComparison.Ordinal))
            {
                var baseName = lower[..^slot.Length]; var label = slot == "helmet" ? "Neuroptics" : char.ToUpperInvariant(slot[0]) + slot[1..];
                foreach (var side in new[] { "left", "right", "upper", "lower" })
                    if (baseName.EndsWith(side, StringComparison.Ordinal)) { baseName = baseName[..^side.Length]; label = char.ToUpperInvariant(side[0]) + side[1..] + " " + label; break; }
                if (baseName.EndsWith("part", StringComparison.Ordinal)) baseName = baseName[..^4];
                return (baseName, label);
            }
        return null;
    }

    public static Catalog Build(IReadOnlyDictionary<string, JsonNode> raw)
    {
        var cat = new Catalog();
        IEnumerable<JsonNode> File(string name) => J.A(raw.GetValueOrDefault(name));
        foreach (var (category, data) in raw)
            foreach (var x in J.A(data))
            {
                if (J.S(x, "uniqueName") is not { } uid || J.S(x, "name") is not { } name) continue;
                cat.Names[uid] = name;
                if (category != "Relics")
                {
                    cat.Lookup.TryAdd(uid, new(uid, name, category, J.S(x, "imageName")));
                    if (J.S(x, "imageName") is { } image) cat.Art.TryAdd(ArtKey(name), image);
                }
            }
        foreach (var (uid, recipe) in J.O(raw.GetValueOrDefault("ExportRecipes")))
        {
            if (J.S(recipe, "resultType") is not { } result || J.Get(recipe, "ingredients") is not JsonArray ingredients) continue;
            var needs = new List<CraftNeed>(); var valid = true;
            foreach (var need in ingredients)
            {
                if (J.S(need, "ItemType") is not { } id || J.N(need, "ItemCount") <= 0) { valid = false; break; }
                needs.Add(new(id, cat.Names.GetValueOrDefault(id) ?? CamelName(id), J.N(need, "ItemCount")));
            }
            if (valid) cat.Recipes[uid] = new(result, Math.Max(1, J.N(recipe, "num", 1)), J.Get(recipe, "buildPrice") == null ? null : J.N(recipe, "buildPrice"),
                J.Get(recipe, "buildTime") == null ? null : J.N(recipe, "buildTime"), needs);
        }
        var partInfo = new Dictionary<string, (string Name, string? Slug)>();
        foreach (var r in File("Relics"))
        {
            var full = J.S(r, "name") ?? ""; var split = full.LastIndexOf(' ');
            if (split < 0 || !Refinements.Contains(full[(split + 1)..]) || J.S(r, "uniqueName") is not { } uid) continue;
            var name = full[..split]; var refinement = full[(split + 1)..];
            if (!cat.Relics.TryGetValue(name, out var relic)) cat.Relics[name] = relic = new(name);
            relic.Uids[refinement] = uid; cat.RelicUids[uid] = (name, refinement);
            if (refinement == "Intact")
            {
                relic.Vaulted = J.Get(r, "vaulted") == null || J.B(J.Get(r, "vaulted"));
                relic.Image = J.S(r, "imageName"); relic.Slug = J.S(J.Get(r, "marketInfo"), "urlName") ?? Slugify(name + " Relic");
            }
            foreach (var rw in J.A(r, "rewards"))
            {
                var item = J.Get(rw, "item"); if (J.S(item, "uniqueName") is not { } rid) continue;
                if (!relic.Rewards.TryGetValue(rid, out var reward)) relic.Rewards[rid] = reward = new(rid, J.S(item, "name") ?? CamelName(rid), J.S(J.Get(item, "warframeMarket"), "urlName"), J.S(rw, "rarity"));
                reward.Chance[refinement] = J.D(J.Get(rw, "chance"));
                if (refinement == "Intact") reward.Rarity = J.S(rw, "rarity");
                partInfo.TryAdd(rid, (reward.Name, reward.Slug)); cat.Names.TryAdd(rid, reward.Name);
            }
        }
        var components = new Dictionary<string, Dictionary<string, long>>();
        foreach (var file in MasteryFiles)
            foreach (var x in File(file))
            {
                if (!J.B(J.Get(x, "masterable")) || J.S(x, "uniqueName") is not { } uid) continue;
                var category = J.S(x, "category") ?? file; var kind = J.S(x, "type") ?? category;
                var weapon = new[] { "Primary", "Secondary", "Melee", "Arch-Gun", "Arch-Melee", "SentinelWeapons" }.Contains(category) || kind == "Kitgun Component";
                cat.Items[uid] = new() { Uid = uid, Name = J.S(x, "name") ?? CamelName(uid), Category = category, Kind = kind,
                    IsPrime = J.B(J.Get(x, "isPrime")), Vaulted = J.B(J.Get(x, "vaulted")), MasteryReq = J.N(x, "masteryReq"),
                    MaxRank = kind == "Necramech" ? 40 : J.N(x, "maxLevelCap") > 0 ? J.N(x, "maxLevelCap") : 30, XpFactor = weapon ? 500 : 1000,
                    Image = J.S(x, "imageName"), Disposition = J.D(J.Get(x, "omegaAttenuation")), DispoDots = J.Get(x, "disposition") == null ? null : J.N(x, "disposition"),
                    RivenClass = category is "Arch-Gun" or "Arch-Melee" || J.S(x, "productCategory") is "SpaceGuns" or "SpaceMelee" ? "archgun" : category == "Melee" || J.S(x, "productCategory") == "Melee" || kind == "Zaw Component" ? "melee" : kind == "Shotgun" ? "shotgun" : J.S(x, "productCategory") == "Pistols" || kind is "Pistol" or "Dual Pistols" or "Throwing" ? "pistol" : "rifle" };
                var list = new Dictionary<string, long>();
                foreach (var c in J.A(x, "components")) if (J.S(c, "uniqueName") is { } id) list[id] = checked(list.GetValueOrDefault(id) + Math.Max(1, J.N(c, "itemCount", 1)));
                components[uid] = list;
            }
        cat.Items.TryAdd(Plexus, new() { Uid = Plexus, Name = "Plexus", Category = "Misc", Kind = "Plexus" });
        var ownBlueprint = new Dictionary<string, string>();
        foreach (var (uid, comps) in components)
        {
            var blueprints = comps.Keys.Where(c => c.Contains("/Recipes/", StringComparison.Ordinal) && c.EndsWith("Blueprint", StringComparison.Ordinal)).Select(c => c.Split('/')[^1][..^9]).ToArray();
            var own = blueprints.Length == 1 ? blueprints[0] : blueprints.FirstOrDefault(c => SplitSlot(c) == null);
            if (own != null) ownBlueprint[uid] = own.ToLowerInvariant();
        }
        foreach (var it in cat.Items.Values)
            foreach (var (uid, count) in components.GetValueOrDefault(it.Uid) ?? [])
            {
                if (cat.Items.TryGetValue(uid, out var other)) { it.Components.Add(new(uid, count, "item", other.Name)); continue; }
                if (!uid.Contains("/Recipes/", StringComparison.Ordinal)) { it.Components.Add(new(uid, count, "resource", cat.NameOf(uid))); continue; }
                var alt = AltUid(uid); var canon = alt != null && !partInfo.ContainsKey(uid) && partInfo.ContainsKey(alt) ? alt : uid;
                var stem = Regex.Replace(canon.Split('/')[^1], "(Blueprint|Component)$", ""); var mine = ownBlueprint.GetValueOrDefault(it.Uid);
                var isBlueprint = canon.EndsWith("Blueprint", StringComparison.Ordinal); string name;
                if (isBlueprint && (mine == stem.ToLowerInvariant() || stem == it.Uid.Split('/')[^1])) name = it.Name + " Blueprint";
                else if (cat.Names.TryGetValue(canon, out var known)) name = known;
                else if (SplitSlot(stem) is { } slot)
                {
                    var owner = mine == null || slot.Base.Length > 0 && (mine.StartsWith(slot.Base, StringComparison.Ordinal) || slot.Base.StartsWith(mine, StringComparison.Ordinal)) ? it.Name :
                        ownBlueprint.Where(p => p.Value == slot.Base).Select(p => cat.Items[p.Key].Name).FirstOrDefault();
                    name = owner == null ? CamelName(canon) : owner + " " + slot.Slot;
                }
                else name = isBlueprint && mine == null ? it.Name + " Blueprint" : CamelName(canon);
                if (partInfo.TryGetValue(canon, out var info)) name = info.Name;
                if (!cat.Parts.TryGetValue(canon, out var part)) cat.Parts[canon] = part = new(canon, name, info.Slug, canon == uid ? alt : uid);
                if (!part.Items.Contains(it.Uid)) part.Items.Add(it.Uid);
                cat.Aliases[canon] = canon; cat.Aliases[uid] = canon; if (alt != null) cat.Aliases.TryAdd(alt, canon);
                it.Components.Add(new(canon, count, "part", name));
            }
        foreach (var relic in cat.Relics.Values)
            foreach (var reward in relic.Rewards.Values)
            {
                reward.Uid = cat.Aliases.GetValueOrDefault(reward.Uid) ?? reward.Uid;
                if (!cat.Parts.TryGetValue(reward.Uid, out var part))
                {
                    var alt = AltUid(reward.Uid); cat.Parts[reward.Uid] = part = new(reward.Uid, reward.Name, reward.Slug, alt);
                    cat.Aliases[reward.Uid] = reward.Uid; if (alt != null) cat.Aliases.TryAdd(alt, reward.Uid);
                }
                part.Relics.Add(new(relic.Name, reward.Rarity, reward.Chance.GetValueOrDefault("Intact"), relic.Vaulted));
            }
        foreach (var part in cat.Parts.Values) part.Relics.Sort((a, b) => a.Vaulted.CompareTo(b.Vaulted) is var c && c != 0 ? c : (b.Chance ?? 0).CompareTo(a.Chance ?? 0));
        foreach (var (category, data) in raw.Where(p => p.Key is not ("Relics" or "Mods")))
            foreach (var x in J.A(data))
            {
                if (J.S(x, "uniqueName") is not { } uid || cat.Items.ContainsKey(uid) || cat.Crafts.Any(c => c.Uid == uid)) continue;
                var needs = J.A(x, "components").Where(c => J.S(c, "uniqueName") != null).Select(c => {
                    var id = J.S(c, "uniqueName")!; return new CraftNeed(id, cat.Names.GetValueOrDefault(id) ?? (id.EndsWith("Blueprint", StringComparison.Ordinal) ? "Blueprint" : CamelName(id)), Math.Max(1, J.N(c, "itemCount", 1)), cat.Lookup.GetValueOrDefault(id)?.Image); }).ToList();
                if (needs.Count > 0) cat.Crafts.Add(new(uid, J.S(x, "name") ?? CamelName(uid), J.S(x, "type") ?? category, J.S(x, "imageName"), J.N(x, "masteryReq"), Math.Max(1, J.N(x, "buildQuantity", 1)), needs));
            }
        foreach (var need in cat.Items.Values.SelectMany(i => i.Components).Where(c => c.Kind == "resource").Select(c => new CraftNeed(c.Uid, c.Name, c.Count))
            .Concat(cat.Recipes.Values.SelectMany(r => r.Ingredients)).Concat(cat.Crafts.SelectMany(c => c.Needs)))
            if (!cat.Items.ContainsKey(need.Uid) && !cat.Aliases.ContainsKey(need.Uid) && !need.Uid.Contains("/Recipes/", StringComparison.Ordinal) && !need.Uid.EndsWith("Blueprint", StringComparison.Ordinal)) cat.Resources.TryAdd(need.Uid, need.Name);
        cat.IndexCraftingUses();
        return cat;
    }

    // Build once per catalog refresh; inventory requests never scan every recipe.
    private void IndexCraftingUses()
    {
        var uses = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        void Add(string ingredient, string result, long count)
        {
            if (ingredient == result || count <= 0) return;
            if (!uses.TryGetValue(ingredient, out var results)) uses[ingredient] = results = new(StringComparer.Ordinal);
            results[result] = Math.Max(results.GetValueOrDefault(result), count);
        }
        foreach (var item in Items.Values)
            foreach (var need in item.Components) Add(need.Uid, item.Uid, need.Count);
        foreach (var craft in Crafts)
            foreach (var need in craft.Needs) Add(need.Uid, craft.Uid, need.Count);
        foreach (var recipe in Recipes.Values)
            foreach (var need in recipe.Ingredients.GroupBy(n => n.Uid)) Add(need.Key, recipe.Result, need.Sum(n => n.Count));
        foreach (var (ingredient, results) in uses)
            CraftingUses[ingredient] = results.Select(r => new CraftUse(r.Key, NameOf(r.Key), r.Value)).OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
    }
}
