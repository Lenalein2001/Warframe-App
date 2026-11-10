using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed record ItemStatus(long Xp, long Rank, string Status, bool Manual, long? Owned, bool Building);
public sealed record PartView(string Uid, string Name, long Need, long Have, string? Slug, long? Price, long? Ducats, bool Vaulted, string? Image, IEnumerable<PartRelic> Relics);

public sealed partial class GameView(Catalog catalog, InventorySnapshot? inventory, Profile? profile, ManualState manual, MarketService market)
{
    public Catalog Catalog { get; } = catalog;
    public InventorySnapshot? Inventory { get; } = inventory;
    public Profile? Profile { get; } = profile;
    public ManualState Manual { get; } = manual;
    public MarketService Market { get; } = market;
    public HashSet<string> Foundry { get; } = inventory?.Foundry.Select(f => f.Recipe).ToHashSet() ?? [];
    public long HeldStack(string uid) => Inventory?.Counts.GetValueOrDefault(uid) ?? 0;
    public long? HandCount(string uid) => Manual.Counts.TryGetValue(uid, out var c) && (Inventory == null || c.Time > (Inventory.SourceTimestamp ?? Inventory.ReadAt.ToUnixTimeMilliseconds() / 1000.0)) ? c.Count : null;
    public long Count(string uid)
    {
        var value = HandCount(uid) ?? HeldStack(uid);
        return Catalog.Parts.GetValueOrDefault(uid)?.Alt is { } alt ? checked(value + (HandCount(alt) ?? HeldStack(alt))) : value;
    }
    public long Xp(string uid) => Math.Max(Profile?.Xp.GetValueOrDefault(uid) ?? 0, Math.Max(Inventory?.Xp.GetValueOrDefault(uid) ?? 0, Inventory?.Equipment.GetValueOrDefault(uid)?.Xp ?? 0));
    public long? Owned(string uid) => Inventory == null ? null : Inventory.Equipment.GetValueOrDefault(uid)?.Count ?? 0;
    public ItemStatus Status(GameItem it)
    {
        var xp = Xp(it.Uid); var rank = Math.Min(it.MaxRank, (long)Math.Sqrt(Math.Max(0, xp) / it.XpFactor)); var marked = Manual.Mastered.ContainsKey(it.Uid);
        return new(xp, marked ? it.MaxRank : rank, marked || rank >= it.MaxRank ? "mastered" : xp > 0 ? "leveling" : "missing", marked, Owned(it.Uid),
            it.Components.Any(c => c.Kind == "part" && Foundry.Contains(c.Uid) && c.Name.EndsWith(it.Name + " Blueprint", StringComparison.Ordinal)));
    }
    public PartRecipe? RecipeForPart(string uid)
    {
        var part = Catalog.Parts.GetValueOrDefault(uid); if (part == null) return null;
        var ids = new[] { uid, part.Alt }.OfType<string>().ToArray();
        return ids.Select(id => Catalog.Recipes.GetValueOrDefault(id)).OfType<PartRecipe>().FirstOrDefault(r => ids.Contains(r.Result));
    }
    public string? ImageFor(string uid)
    {
        var canon = Catalog.Aliases.GetValueOrDefault(uid) ?? uid;
        if (Catalog.Parts.TryGetValue(canon, out var part))
        {
            var slug = Market.Slug(canon, part.Slug, part.Name);
            return Market.Item(slug)?.Image ?? part.Items.Select(Catalog.ImageFor).FirstOrDefault(i => i != null) ?? Catalog.ImageFor(uid);
        }
        return Catalog.ImageFor(uid);
    }
    public string? IconOf(string uid, string name) => Catalog.Art.TryGetValue(Services.Catalog.ArtKey(name), out var art) ? Services.Catalog.ImageUrl(art) : Catalog.Inventory.GetValueOrDefault(uid)?.Icon ?? Catalog.ArtByName(name);
    public List<PartView> Parts(GameItem it, bool prices = false) => it.Components.Where(c => c.Kind == "part").Select(c => {
        var p = Catalog.Parts.GetValueOrDefault(c.Uid); var slug = Market.Slug(c.Uid, p?.Slug, p?.Name);
        return new PartView(c.Uid, c.Name, c.Count, Count(c.Uid), slug, Market.Price(slug, prices)?.Sell, Market.Item(slug)?.Ducats, p?.Vaulted ?? false, ImageFor(c.Uid), p?.Relics.Take(6) ?? []); }).ToList();
    public Dictionary<string, long> NeededParts()
    {
        var result = new Dictionary<string, long>();
        foreach (var it in Catalog.Items.Values.Where(i => i.IsPrime))
        {
            var st = Status(it); if (st.Status == "mastered" || st.Owned > 0 || st.Building) continue;
            foreach (var c in it.Components.Where(c => c.Kind == "part")) result[c.Uid] = checked(result.GetValueOrDefault(c.Uid) + c.Count);
        }
        return result;
    }
    public static (long Rank, long Floor, long Next) RankFor(long points)
    {
        long rank = 0; while (rank < 30 && 2500 * (rank + 1) * (rank + 1) <= points) rank++;
        if (rank < 30) return (rank, 2500 * rank * rank, 2500 * (rank + 1) * (rank + 1));
        var legendary = Math.Max(0, (points - 2250000) / 147500);
        return (30 + legendary, 2250000 + 147500 * legendary, 2250000 + 147500 * (legendary + 1));
    }
    public Progress? Progress()
    {
        var sources = new[] { Profile?.Progress, Inventory?.Progress }.OfType<Progress>().ToArray(); if (sources.Length == 0) return null;
        var nodes = new Dictionary<string, long>(); var skills = new Dictionary<string, long>();
        foreach (var p in sources) { foreach (var (key, n) in p.Nodes) nodes[key] = Math.Max(nodes.GetValueOrDefault(key), n); foreach (var (key, n) in p.Intrinsics) skills[key] = Math.Max(skills.GetValueOrDefault(key), n); }
        return new(nodes, skills);
    }
    public JsonNode MasteryProgress()
    {
        var categories = Catalog.Items.Values.GroupBy(i => i.Category).Select(g => new { category = g.Key, mastered = g.Count(i => Status(i).Status == "mastered"), total = g.Count(),
            points = g.Sum(i => Status(i).Rank * (long)(i.XpFactor / 5)), possible = g.Sum(i => i.MaxRank * (long)(i.XpFactor / 5)) }).OrderByDescending(g => g.total).ToArray();
        var points = categories.Sum(c => c.points); var possible = categories.Sum(c => c.possible); var progress = Progress();
        long starChart = 0, steelPath = 0, railjack = 0, drifter = 0;
        if (progress != null)
        {
            foreach (var (tag, tier) in progress.Nodes) { var xp = tag.EndsWith("Junction", StringComparison.Ordinal) ? 1000 : Catalog.NodeXp.GetValueOrDefault(tag); starChart += xp; if (tier >= 1) steelPath += xp; }
            foreach (var (skill, rank) in progress.Intrinsics) { var xp = Math.Max(0, rank) * 1500; if (skill.StartsWith("LPS_DRIFT_", StringComparison.Ordinal)) drifter += xp; else railjack += xp; }
        }
        var total = points + starChart + steelPath + railjack + drifter; var ranks = RankFor(total);
        return J.Node(new { points, possible, total, lacks = progress == null ? "profile" : Catalog.NodeXp.Count == 0 ? "starChart" : null, rank = ranks.Rank, rankFloor = ranks.Floor, nextAt = ranks.Next,
            sources = new { gear = points, starChart, steelPath, railjack, drifter }, byCategory = categories });
    }
    public JsonArray Mastery() => J.Array(Catalog.Items.Values.OrderBy(i => i.Category, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal).Select(it =>
    {
        var st = Status(it); var row = (JsonObject)J.Node(new { uid = it.Uid, name = it.Name, category = it.Category, type = it.Kind, isPrime = it.IsPrime, vaulted = it.Vaulted,
            maxRank = it.MaxRank, masteryReq = it.MasteryReq, image = Services.Catalog.ImageUrl(it.Image), points = st.Rank * (long)(it.XpFactor / 5), maxPoints = it.MaxRank * (long)(it.XpFactor / 5) });
        MergeStatus(row, st);
        if (st.Status != "mastered")
        {
            var parts = Parts(it); row["parts"] = J.Array(parts.Select(p => new { name = p.Name, need = p.Need, have = p.Have, vaulted = p.Vaulted, image = p.Image }));
            row["buildable"] = parts.Count > 0 && parts.All(p => p.Have >= p.Need); row["requires"] = J.Array(it.Components.Where(c => c.Kind == "item").Select(c => c.Name));
            row["farm"] = J.Array(parts.Where(p => p.Have < p.Need && p.Relics.Any()).Select(p => { var live = p.Relics.Where(r => !r.Vaulted).ToArray(); return new { part = p.Name, relics = (live.Length > 0 ? live : p.Relics).Take(3).Select(r => $"{r.Relic} ({r.Rarity ?? "?"})"), vaulted = live.Length == 0 }; }));
        }
        return row;
    }));
    private static void MergeStatus(JsonObject row, ItemStatus st) { foreach (var (key, value) in (JsonObject)J.Node(st)) row[key] = value?.DeepClone(); }
    public JsonArray Primes() => EquipmentBuilds(true);
    public JsonArray Craftable() => EquipmentBuilds(false);
    private JsonArray EquipmentBuilds(bool primes)
    {
        var rows = new List<JsonObject>();
        foreach (var it in Catalog.Items.Values.Where(i => !primes || i.IsPrime))
        {
            var parts = Parts(it, primes); if (parts.Count == 0) continue;
            var st = Status(it); var have = parts.Sum(p => Math.Min(p.Have, p.Need)); var need = parts.Sum(p => p.Need);
            var row = (JsonObject)J.Node(new { uid = it.Uid, name = it.Name, category = it.Category, vaulted = it.Vaulted, image = Services.Catalog.ImageUrl(it.Image), status = st.Status,
                owned = st.Owned, parts, have, need, complete = have >= need });
            if (primes)
            {
                var setSlug = Market.Slug(null, it.SetSlug, it.Name + " Set"); row["rank"] = st.Rank; row["setPrice"] = Market.Price(setSlug)?.Sell; row["setSlug"] = setSlug;
                row["requires"] = J.Array(it.Components.Where(c => c.Kind == "item").Select(c => c.Name));
            }
            else { row["isPrime"] = it.IsPrime; row["masteryReq"] = it.MasteryReq; row["buildable"] = parts.All(p => p.Have >= p.Need); }
            rows.Add(row);
        }
        return J.Array(rows.OrderByDescending(r => J.N(r, "have") / (double)Math.Max(1, J.N(r, "need"))).ThenBy(r => J.S(r, "name"), StringComparer.Ordinal));
    }
    public JsonArray Crafts() => J.Array(Catalog.Crafts.Select(c => {
        var parts = c.Needs.Select(n => new { name = n.Name, need = n.Count, have = Count(n.Uid), image = Services.Catalog.ImageUrl(n.Image) }).ToArray(); var met = parts.Count(p => p.have >= p.need);
        return new { uid = c.Uid, name = c.Name, category = c.Kind, craft = true, pinned = Manual.Pinned.Contains(c.Uid), image = Services.Catalog.ImageUrl(c.Image), masteryReq = c.MasteryReq,
            quantity = c.Quantity, held = Count(c.Uid), have = met, need = parts.Length, buildable = met == parts.Length, parts }; }).OrderByDescending(c => c.have / (double)Math.Max(1, c.need)));
    public JsonArray PendingFoundry() => J.Array((Inventory?.Foundry ?? []).OrderBy(f => f.Done ?? double.MaxValue).Select(f => new { uid = f.Recipe, name = Catalog.NameOf(f.Recipe), done = f.Done, image = ImageFor(f.Recipe), known = Catalog.Items.ContainsKey(f.Recipe) || Catalog.Aliases.ContainsKey(f.Recipe) }));
    public JsonNode InventoryRows()
    {
        if (Inventory == null) return J.Node(new { items = Array.Empty<object>(), source = (string?)null });
        var entries = Inventory.Equipment.Select(p => (p.Key, p.Value.Count)).Concat(Inventory.Counts.Where(p => p.Value > 0 && !Inventory.Equipment.ContainsKey(p.Key)).Select(p => (p.Key, p.Value)));
        var rows = entries.Select(p => { var (name, kind, image) = InventoryEntry(p.Key); var slug = Market.Slug(p.Key, null, name); if (Market.Item(slug) == null) slug = null;
            return new { uid = p.Key, name, kind, count = p.Item2, image, price = Market.Price(slug, false)?.Sell, ducats = Market.Item(slug)?.Ducats, slug }; });
        return J.Node(new { items = rows, source = Inventory.Provider, imported = Inventory.ReadAt.ToUnixTimeMilliseconds() / 1000.0, currencies = Inventory.Currencies });
    }
    private (string Name, string Kind, string? Image) InventoryEntry(string uid, HashSet<string>? visiting = null)
    {
        visiting ??= []; if (!visiting.Add(uid)) return (Catalog.NameOf(uid), "Other", null);
        var export = Catalog.Inventory.GetValueOrDefault(uid); var icon = export?.Icon;
        if (export?.Kind is "Resources" or "Gems and ores" or "Fish" && export.Name != null && Catalog.Art.TryGetValue(Services.Catalog.ArtKey(export.Name), out var art)) icon = Services.Catalog.ImageUrl(art);
        if (Catalog.Items.TryGetValue(uid, out var it)) return (it.Name, it.Category, Services.Catalog.ImageUrl(it.Image) ?? icon);
        if (export?.Name is { } name && export.Result == null) return (name, export.Kind, icon);
        var canon = Catalog.Aliases.GetValueOrDefault(uid);
        if (canon != null && Catalog.Parts.TryGetValue(canon, out var part)) return (part.Name + (uid.EndsWith("Blueprint", StringComparison.Ordinal) && !part.Name.EndsWith("Blueprint", StringComparison.Ordinal) ? " Blueprint" : ""), part.Name.Contains("Prime", StringComparison.Ordinal) ? "Prime parts" : "Parts", ImageFor(uid) ?? icon);
        if (Catalog.RelicUids.TryGetValue(uid, out var relic)) return ($"{relic.Name} {relic.Refinement}", "Relics", Services.Catalog.ImageUrl(Catalog.Relics[relic.Name].Image) ?? icon);
        if (export != null)
        {
            if (export.Name != null) return (export.Name, export.Kind, icon);
            if (export.Result is { } result) { var built = InventoryEntry(result, visiting); return (built.Name + " Blueprint", export.Kind, icon ?? built.Image); }
        }
        return (Catalog.NameOf(uid), uid.Contains("/Upgrades/Mods/", StringComparison.Ordinal) ? "Mods" : uid.Contains("/Weapons/", StringComparison.Ordinal) ? "Weapons" : "Other", icon);
    }
    public JsonObject RelicRow(GameRelic relic, Dictionary<string, long>? needed = null)
    {
        needed ??= NeededParts(); var counts = relic.Uids.ToDictionary(p => p.Key, p => Count(p.Value)); var total = counts.Values.Sum();
        var ev = new Dictionary<string, double> { ["Intact"] = 0, ["Radiant"] = 0 };
        var rewards = relic.Rewards.Values.OrderBy(r => r.Rarity switch { "Common" => 0, "Uncommon" => 1, "Rare" => 2, _ => 3 }).Select(r => {
            var part = Catalog.Parts.GetValueOrDefault(r.Uid); var slug = Market.Slug(r.Uid, r.Slug ?? part?.Slug, r.Name); var price = Market.Price(slug, total > 0)?.Sell;
            foreach (var refine in ev.Keys.ToArray()) if (r.Chance.GetValueOrDefault(refine) is { } chance && price.HasValue) ev[refine] += chance / 100 * price.Value;
            return new { name = r.Name, uid = r.Uid, rarity = r.Rarity, chance = r.Chance, price, ducats = Market.Item(slug)?.Ducats, image = Market.Item(slug)?.Image, have = Count(r.Uid), needed = needed.GetValueOrDefault(r.Uid) }; }).ToArray();
        var relicSlug = Market.Slug(null, relic.Slug, relic.Name + " Relic");
        return (JsonObject)J.Node(new { name = relic.Name, era = relic.Era, vaulted = relic.Vaulted, counts, image = Services.Catalog.ImageUrl(relic.Image), total, uids = relic.Uids,
            rewards, needsCount = rewards.Count(r => r.needed > r.have), slug = relicSlug, ev = ev.ToDictionary(p => p.Key, p => Math.Round(p.Value, 1, MidpointRounding.ToEven)), price = Market.Price(relicSlug, total > 0)?.Sell });
    }
    public JsonArray Relics(bool ownedOnly)
    {
        var needed = NeededParts(); var eras = new[] { "Lith", "Meso", "Neo", "Axi", "Requiem" };
        return J.Array(Catalog.Relics.Values.OrderBy(r => Array.IndexOf(eras, r.Era)).ThenBy(r => r.Name, StringComparer.Ordinal).Select(r => RelicRow(r, needed)).Where(r => !ownedOnly || J.N(r, "total") > 0));
    }
    public JsonNode RelicPicks()
    {
        var rows = J.A(Relics(true)).ToArray(); var result = new JsonObject();
        foreach (var group in rows.GroupBy(r => J.S(r, "era")!))
        {
            var best = group.OrderByDescending(r => J.N(r, "needsCount")).ThenByDescending(r => J.D(J.Get(J.Get(r, "ev"), "Radiant")) ?? 0).First();
            result[group.Key] = J.Node(new { owned = group.Sum(r => J.N(r, "total")), kinds = group.Count(), best = new { name = J.S(best, "name"), needs = J.N(best, "needsCount"), ev = J.D(J.Get(J.Get(best, "ev"), "Radiant")), image = J.S(best, "image"), total = J.N(best, "total") } });
        }
        return result;
    }
    public JsonArray Trade()
    {
        var needed = NeededParts();
        return J.Array(Catalog.Parts.Values.Where(p => p.Relics.Count > 0 && p.Name.Contains("Prime", StringComparison.Ordinal)).Select(p => {
            var have = Count(p.Uid); var spare = have - needed.GetValueOrDefault(p.Uid); var slug = Market.Slug(p.Uid, p.Slug, p.Name); var price = Market.Price(slug)?.Sell; var ducats = Market.Item(slug)?.Ducats;
            return new { uid = p.Uid, name = p.Name, have, spare, slug, price, ducats, vaulted = p.Vaulted, ducatsPerPlat = ducats > 0 && price > 0 ? Math.Round(ducats.Value / (double)price.Value, 1) : (double?)null, image = Market.Item(slug)?.Image }; }).Where(r => r.have > 0 && r.spare > 0).OrderByDescending(r => (r.price ?? 0) * r.spare));
    }
    public JsonNode Collection()
    {
        var counts = Inventory?.Counts ?? []; var sets = Catalog.Items.Values.Where(i => i.IsPrime).Select(i => Parts(i)).Where(p => p.Count > 0).ToArray();
        return J.Node(new { relics = counts.Where(p => Catalog.RelicUids.ContainsKey(p.Key) && p.Value > 0).Sum(p => p.Value), relicKinds = counts.Count(p => Catalog.RelicUids.ContainsKey(p.Key) && p.Value > 0),
            primeParts = counts.Where(p => p.Value > 0 && Catalog.Parts.GetValueOrDefault(Catalog.Aliases.GetValueOrDefault(p.Key) ?? "")?.Name.Contains("Prime", StringComparison.Ordinal) == true).Sum(p => p.Value),
            setsComplete = sets.Count(p => p.All(c => c.Have >= c.Need)), setsStarted = sets.Count(p => p.Any(c => c.Have >= c.Need) && p.Any(c => c.Have < c.Need)), mods = Inventory?.Mods ?? 0, stacks = counts.Count });
    }
    public JsonArray Pinned()
    {
        var needed = NeededParts(); var result = new JsonArray();
        foreach (var key in Manual.Pinned)
        {
            if (Catalog.Items.TryGetValue(key, out var it)) { var parts = Parts(it); result.Add(J.Node(new { key, kind = "item", name = it.Name, open = true, image = Services.Catalog.ImageUrl(it.Image), status = Status(it).Status, have = parts.Sum(p => Math.Min(p.Have, p.Need)), need = parts.Sum(p => p.Need), unit = "parts" })); }
            else if (Catalog.Parts.TryGetValue(key, out var p)) result.Add(J.Node(new { key, kind = "part", name = p.Name, open = true, image = ImageFor(key), have = Count(key), need = needed.GetValueOrDefault(key), unit = "held" }));
            else if (Catalog.Relics.TryGetValue(key, out var relic)) result.Add(J.Node(new { key, kind = "relic", name = key + " Relic", open = true, image = Services.Catalog.ImageUrl(relic.Image), have = relic.Uids.Values.Sum(Count), need = 0, unit = "held" }));
            else if (Catalog.Crafts.FirstOrDefault(c => c.Uid == key) is { } craft) result.Add(J.Node(new { key, kind = "build", name = craft.Name, open = false, image = Services.Catalog.ImageUrl(craft.Image), have = craft.Needs.Count(n => Count(n.Uid) >= n.Count), need = craft.Needs.Count, unit = "needs met" }));
        }
        return result;
    }
    public static int? MatchRank(string name, string query)
    {
        var lower = name.ToLowerInvariant(); var q = query.Trim().ToLowerInvariant();
        if (lower == q) return 0; if (lower.StartsWith(q, StringComparison.Ordinal)) return 1;
        var words = Regex.Split(lower, @"[^\p{L}\p{N}]+");
        if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal)))) return 2;
        return lower.Contains(q, StringComparison.Ordinal) ? 3 : null;
    }
    public JsonArray Find(string query)
    {
        if (query.Trim().Length < 2) return [];
        var hits = new List<JsonNode>();
        foreach (var it in Catalog.Items.Values) if (MatchRank(it.Name, query) != null) hits.Add(J.Node(new { kind = "item", key = it.Uid, name = it.Name, sub = it.Category, image = Services.Catalog.ImageUrl(it.Image), status = Status(it).Status }));
        foreach (var p in Catalog.Parts.Values.Where(p => p.Relics.Count > 0)) if (MatchRank(p.Name, query) != null) { var slug = Market.Slug(p.Uid, p.Slug, p.Name); hits.Add(J.Node(new { kind = "part", key = p.Uid, name = p.Name, sub = p.Vaulted ? "Prime part · vaulted" : "Prime part", image = Market.Item(slug)?.Image, price = Market.Price(slug, false)?.Sell, have = Count(p.Uid) })); }
        foreach (var r in Catalog.Relics.Values) if (MatchRank(r.Name + " Relic", query) != null) hits.Add(J.Node(new { kind = "relic", key = r.Name, name = r.Name + " Relic", sub = r.Vaulted ? "Relic · vaulted" : "Relic", image = Services.Catalog.ImageUrl(r.Image), have = r.Uids.Values.Sum(Count) }));
          foreach (var s in J.A(new StandingService(this).Summary(null, null), "syndicates"))
              if (J.S(s, "name") is { } name && MatchRank(name, query) != null) hits.Add(J.Node(new { kind = "syndicate", key = "syndicate:" + J.S(s, "tag"), name, sub = "Syndicate · standing and offerings", image = J.S(s, "icon") }));
          var represented = hits.Select(h => J.S(h, "name")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var it in Catalog.Lookup.Values) if (!Catalog.Items.ContainsKey(it.Uid) && MatchRank(it.Name, query) != null && represented.Add(it.Name)) hits.Add(J.Node(new { kind = "lookup", key = "lookup:" + it.Uid, name = it.Name, sub = it.Category, image = Services.Catalog.ImageUrl(it.Image) }));
          foreach (var name in Catalog.Drops.Names) if (MatchRank(name, query) != null && represented.Add(name)) hits.Add(J.Node(new { kind = "drop", key = "drop:" + name, name, sub = "Drop sources", image = Catalog.ArtByName(name) }));
          return J.Array(hits.OrderBy(h => MatchRank(J.S(h, "name")!, query)).ThenBy(h => J.S(h, "name")!.Length).ThenBy(h => J.S(h, "name"), StringComparer.Ordinal).Take(24));
    }
}
