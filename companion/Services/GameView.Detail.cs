using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed partial class GameView
{
    public JsonNode Coverage => J.Node(new { available = Catalog.Drops.Available, total = Catalog.Drops.Total });
    public JsonNode? PartRecipe(string uid)
    {
        var recipe = RecipeForPart(uid); if (recipe == null) return null;
        return J.Node(new { quantity = recipe.Quantity, credits = recipe.Credits, seconds = recipe.Seconds, ingredients = recipe.Ingredients.Select(n => new {
            uid = n.Uid, name = n.Name, count = n.Count, image = IconOf(n.Uid, n.Name), have = Inventory != null || Manual.Counts.ContainsKey(n.Uid) ? Count(n.Uid) : (long?)null, drops = Catalog.Drops.ForName(n.Name) }) });
    }
    public JsonObject? Detail(string key, HashSet<string>? openEras = null)
    {
        openEras ??= [];
        bool Open(string era) => openEras.Contains(era) || openEras.Contains("Omnia");
        JsonArray References(IEnumerable<PartRelic> relics) => J.Array(relics.Select(r => {
            var rel = Catalog.Relics.GetValueOrDefault(r.Relic); var owned = rel?.Uids.Values.Sum(Count) ?? 0;
            return new { relic = r.Relic, rarity = r.Rarity, chance = r.Chance, vaulted = r.Vaulted, owned, open = Open(rel?.Era ?? "") && (!r.Vaulted || owned > 0) };
        }).OrderBy(r => !r.open).ThenBy(r => r.vaulted).ThenByDescending(r => r.owned).ThenByDescending(r => r.chance ?? 0));
        JsonObject WithCommon(JsonObject row)
        {
            row["pinned"] = Manual.Pinned.Contains(J.S(row, "key") ?? key);
            var uid = J.S(row, "uid") ?? (key.StartsWith("lookup:", StringComparison.Ordinal) ? key[7..] : null);
            if (uid != null && new FarmingPlanner(this).Thing(uid) is { }) row["task"] = J.Node(new { uid, onBoard = Manual.Tasks.Any(t => t.Uid == uid) });
            return row;
        }
        if (key.StartsWith("lookup:", StringComparison.Ordinal) && Catalog.Lookup.GetValueOrDefault(key[7..]) is { } lookup)
            return WithCommon((JsonObject)J.Node(new { kind = "drop", key, name = lookup.Name, image = Services.Catalog.ImageUrl(lookup.Image), drops = Catalog.Drops.ForName(lookup.Name), dropCoverage = Coverage }));
        if (key.StartsWith("drop:", StringComparison.Ordinal))
        {
            var drops = Catalog.Drops.ForName(key[5..]).ToArray(); if (drops.Length == 0) return null;
            return WithCommon((JsonObject)J.Node(new { kind = "drop", key, name = drops[0].Item, image = Catalog.ArtByName(drops[0].Item), drops, dropCoverage = Coverage }));
        }
        if (Catalog.Items.TryGetValue(key, out var it))
        {
            var st = Status(it); var parts = Parts(it, true).Select(p => {
                var node = (JsonObject)J.Node(p); node["relics"] = References(Catalog.Parts.GetValueOrDefault(p.Uid)?.Relics ?? []);
                node["drops"] = J.Array(Catalog.Drops.ForName(p.Name, true)); node["recipe"] = PartRecipe(p.Uid); return node;
            }).ToArray();
            var setSlug = it.SetSlug == null ? null : Market.Slug(null, it.SetSlug, it.Name + " Set");
            var row = (JsonObject)J.Node(new { kind = "item", key = it.Uid, uid = it.Uid, name = it.Name, category = it.Category, type = it.Kind, image = Services.Catalog.ImageUrl(it.Image), isPrime = it.IsPrime, vaulted = it.Vaulted,
                masteryReq = it.MasteryReq, maxRank = it.MaxRank, points = st.Rank * (long)(it.XpFactor / 5), maxPoints = it.MaxRank * (long)(it.XpFactor / 5), setSlug, setPrice = Market.Price(setSlug)?.Sell,
                parts, buildable = parts.Length > 0 && parts.All(p => J.N(p, "have") >= J.N(p, "need")), recipesAvailable = Catalog.RecipesAvailable, drops = Catalog.Drops.ForName(it.Name), dropCoverage = Coverage,
                requires = it.Components.Where(c => c.Kind == "item").Select(c => new { uid = c.Uid, name = c.Name, count = c.Count, status = Catalog.Items.TryGetValue(c.Uid, out var req) ? Status(req).Status : null, have = Owned(c.Uid) }),
                resources = it.Components.Where(c => c.Kind == "resource").Select(c => new { uid = c.Uid, name = c.Name, count = c.Count, have = Inventory != null || Manual.Counts.ContainsKey(c.Uid) ? Count(c.Uid) : (long?)null, image = IconOf(c.Uid, c.Name), drops = Catalog.Drops.ForName(c.Name) }),
                disposition = it.Disposition == null ? null : (double?)Math.Round(it.Disposition.Value, 3), dots = it.DispoDots });
            MergeStatus(row, st); return WithCommon(row);
        }
        var canon = Catalog.Aliases.GetValueOrDefault(key) ?? key;
        if (Catalog.Parts.TryGetValue(canon, out var part))
        {
            var slug = Market.Slug(canon, part.Slug, part.Name);
            return WithCommon((JsonObject)J.Node(new { kind = "part", key = canon, uid = canon, name = part.Name, image = ImageFor(canon), slug, price = Market.Price(slug)?.Sell, ducats = Market.Item(slug)?.Ducats,
                vaulted = part.Vaulted, have = Count(canon), needed = NeededParts().GetValueOrDefault(canon), relics = References(part.Relics), drops = Catalog.Drops.ForName(part.Name, true), recipe = PartRecipe(canon), recipesAvailable = Catalog.RecipesAvailable,
                dropCoverage = Coverage, usedIn = part.Items.Where(Catalog.Items.ContainsKey).Select(uid => Catalog.Items[uid]).Select(i => new { uid = i.Uid, name = i.Name, image = Services.Catalog.ImageUrl(i.Image), status = Status(i).Status }) }));
        }
        var name = key.EndsWith(" Relic", StringComparison.Ordinal) ? key[..^6] : key;
        if (!Catalog.Relics.TryGetValue(name, out var relic)) return null;
        var result = RelicRow(relic); result["kind"] = "relic"; result["key"] = name; result["open"] = Open(relic.Era) && (!relic.Vaulted || J.N(result, "total") > 0);
        result["drops"] = J.Array(Catalog.Drops.ForName(name + " Relic")); result["dropCoverage"] = Coverage;
        return WithCommon(result);
    }
}
