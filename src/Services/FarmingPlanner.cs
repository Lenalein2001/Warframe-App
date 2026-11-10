using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed record FarmingThing(string Kind, string Name, string? Image, string Sub, long? TopRank);
public sealed record PlannedPart(string Uid, string Name, long Need, long Have, long Crafting, long Builds, bool RecipeUnknown);
public sealed record RequiredItem(string Name, long Count, long? Owned);
public sealed record FarmingPlan(List<PlannedPart> Parts, List<RequiredItem> Requires, Dictionary<string, CraftNeed> Materials, long Credits);

public sealed class FarmingPlanner(GameView view)
{
    public FarmingThing? Thing(string uid)
    {
        if (view.Catalog.Items.GetValueOrDefault(uid) is { } item) return new("item", item.Name, Catalog.ImageUrl(item.Image), item.Category, null);
        if (view.Catalog.Inventory.GetValueOrDefault(uid) is { Hidden: false } e && e.Kind is "Mods" or "Arcanes")
        {
            var name = e.Name ?? view.Catalog.Lookup.GetValueOrDefault(uid)?.Name; if (name == null) return null;
            var label = e.Kind == "Mods" ? "Mod" : "Arcane";
            return new(e.Kind == "Mods" ? "mod" : "arcane", name, e.Icon, e.Rarity == null ? label : label + " · " + e.Rarity, e.Rank);
        }
        if (view.Catalog.Lookup.GetValueOrDefault(uid) is { Category: "Mods" } mod) return new("mod", mod.Name, null, "Mod", null);
        if (!view.Catalog.Resources.TryGetValue(uid, out var resource)) return null;
        var resourceName = view.Catalog.Names.GetValueOrDefault(uid) ?? view.Catalog.Inventory.GetValueOrDefault(uid)?.Name ?? resource;
        return new("resource", resourceName, view.IconOf(uid, resourceName), "Resource", null);
    }
    public static long ArcaneCopies(long rank) { var r = Math.Max(0, rank); return checked((r + 1) * (r + 2) / 2); }
    public static long DefaultTarget(FarmingThing thing) => thing.Kind == "arcane" && thing.TopRank.HasValue ? ArcaneCopies(thing.TopRank.Value) : 1;
    public (long Count, List<long>? Ranks, bool ByHand) Held(string uid, string kind)
    {
        if (view.HandCount(uid) is { } hand) return (hand, null, true);
        var ranks = kind is "mod" or "arcane" && view.Inventory?.Upgrades != null ? view.Inventory.Upgrades.GetValueOrDefault(uid) ?? [] : null;
        var ranked = ranks?.Sum(r => kind == "arcane" ? ArcaneCopies(r) : 1) ?? 0;
        return (checked(view.HeldStack(uid) + ranked), ranks, false);
    }
    public FarmingPlan Plan(GameItem item)
    {
        var pending = (view.Inventory?.Foundry ?? []).GroupBy(f => f.Recipe).ToDictionary(g => g.Key, g => (long)g.Count());
        var materials = new Dictionary<string, CraftNeed>(); var parts = new List<PlannedPart>(); long credits = 0;
        void Add(string uid, string name, long n) { if (n > 0) materials[uid] = new(uid, name, checked((materials.GetValueOrDefault(uid)?.Count ?? 0) + n)); }
        foreach (var c in item.Components.Where(c => c.Kind == "part"))
        {
            var alt = view.Catalog.Parts.GetValueOrDefault(c.Uid)?.Alt; var ids = new[] { c.Uid, alt }.OfType<string>().ToArray();
            var crafting = ids.Sum(id => pending.GetValueOrDefault(id));
            long Crafted(string result) => (view.HandCount(c.Uid) != null ? 0 : view.HeldStack(result)) + crafting;
            long builds = 0; var unknown = false;
            if (view.RecipeForPart(c.Uid) is { } recipe)
            {
                var left = Math.Max(0, c.Count - Crafted(recipe.Result)); builds = (left + recipe.Quantity - 1) / recipe.Quantity;
                foreach (var n in recipe.Ingredients) Add(n.Uid, n.Name, checked(n.Count * builds));
                credits = checked(credits + (recipe.Credits ?? 0) * builds);
            }
            else if (ids.FirstOrDefault(id => id.EndsWith("Component", StringComparison.Ordinal)) is { } component && !c.Name.EndsWith(item.Name + " Blueprint", StringComparison.Ordinal)) unknown = Crafted(component) < c.Count;
            parts.Add(new(c.Uid, c.Name, c.Count, view.Count(c.Uid) + crafting, crafting, builds, unknown));
        }
        foreach (var c in item.Components.Where(c => c.Kind == "resource")) Add(c.Uid, c.Name, c.Count);
        var final = item.Components.Where(c => c.Kind == "part").SelectMany(c => new[] { c.Uid, view.Catalog.Parts.GetValueOrDefault(c.Uid)?.Alt }).OfType<string>()
            .Select(id => view.Catalog.Recipes.GetValueOrDefault(id)).OfType<PartRecipe>().FirstOrDefault(r => r.Result == item.Uid);
        credits = checked(credits + (final?.Credits ?? 0));
        return new(parts, item.Components.Where(c => c.Kind == "item").Select(c => new RequiredItem(c.Name, c.Count, view.Owned(c.Uid))).ToList(), materials.OrderBy(p => p.Value.Name, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value), credits);
    }
    public JsonObject Row(FarmingTask task)
    {
        var t = Thing(task.Uid);
        if (t == null) return (JsonObject)J.Node(new { uid = task.Uid, kind = "unknown", name = view.Catalog.NameOf(task.Uid), image = (string?)null, sub = "Unknown", state = "missing", note = "Not in the loaded game data", have = 0, need = 1 });
        string state, note; long have, need;
        if (t.Kind == "item" && view.Catalog.Items.GetValueOrDefault(task.Uid) is { } item)
        {
            var plan = Plan(item); var st = view.Status(item);
            need = plan.Parts.Sum(p => p.Need) + plan.Requires.Sum(r => r.Count); have = plan.Parts.Sum(p => Math.Min(p.Have, p.Need)) + plan.Requires.Sum(r => Math.Min(r.Owned ?? 0, r.Count));
            var missing = plan.Parts.Where(p => p.Have < p.Need).Select(p => p.Name.StartsWith(item.Name, StringComparison.Ordinal) ? p.Name[item.Name.Length..].TrimStart() : p.Name)
                .Concat(plan.Requires.Where(r => (r.Owned ?? 0) < r.Count).Select(r => r.Name)).ToArray();
            var shortfalls = plan.Materials.Values.Where(n => view.Count(n.Uid) < n.Count).Select(n => n.Name).ToList();
            if (view.Inventory?.Currencies.Credits is { } money && money < plan.Credits) shortfalls.Add("Credits");
            var unknown = plan.Parts.Count(p => p.RecipeUnknown);
            if (st.Owned > 0 || st.Building) { state = "done"; note = st.Owned > 0 ? "Owned" : "Building in the foundry"; have = need = 1; }
            else if (need == 0) { state = "missing"; note = "Not owned"; have = 0; need = 1; }
            else if (missing.Length > 0) { state = "missing"; note = missing.Length == 1 ? "Missing " + missing[0] : $"Missing {need - have} of {need} parts"; }
            else if (shortfalls.Count > 0) { state = "resources"; note = shortfalls.Count == 1 ? "Short of " + shortfalls[0] : $"Short of {shortfalls.Count} resources"; need = Math.Max(1, plan.Materials.Count + (plan.Credits > 0 ? 1 : 0)); have = Math.Max(0, need - shortfalls.Count); }
            else if (unknown > 0 || plan.Credits > 0 && view.Inventory?.Currencies.Credits == null)
            { state = "resources"; note = unknown > 0 ? $"Recipes of {unknown} parts unknown" : "Credits unknown"; have = plan.Materials.Count; need = have + Math.Max(1, unknown); }
            else { state = "done"; note = plan.Parts.Any(p => p.Crafting > 0) ? "Ready once its parts are crafted" : "Ready to build"; have = need = 1; }
        }
        else
        {
            var held = Held(task.Uid, t.Kind); var target = task.Target ?? DefaultTarget(t); have = Math.Min(held.Count, target); need = target;
            state = held.Count >= target ? "done" : "missing";
            note = t.Kind is "mod" or "arcane" && target == 1 ? held.Count > 0 ? "Owned" : "Not owned" : held.Count.ToString("N0", CultureInfo.InvariantCulture) + " of " + target.ToString("N0", CultureInfo.InvariantCulture) + (t.Kind is "mod" or "arcane" ? " copies" : "");
        }
        return (JsonObject)J.Node(new { uid = task.Uid, kind = t.Kind, name = t.Name, image = t.Image, sub = t.Sub, state, note, have, need });
    }
    public JsonNode Board(bool summary = false)
    {
        var rows = view.Manual.Tasks.Select(Row).ToArray(); var done = rows.Count(r => J.S(r, "state") == "done");
        var sig = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(J.Array(rows).ToJsonString())))[..16].ToLowerInvariant();
        return summary ? J.Node(new { total = rows.Length, done, sig }) : J.Node(new { tasks = rows, done, sig, inventoryKnown = view.Inventory != null, ranksKnown = view.Inventory?.Upgrades != null });
    }
    public JsonNode? Detail(string uid, HashSet<string>? openEras = null)
    {
        var thing = Thing(uid); if (thing == null) return null;
        var saved = view.Manual.Tasks.FirstOrDefault(t => t.Uid == uid); var task = saved ?? new(uid, null, 0); var row = Row(task);
        row["onBoard"] = saved != null; row["inventoryKnown"] = view.Inventory != null;
        if (thing.Kind == "item" && view.Catalog.Items.GetValueOrDefault(uid) is { } item)
        {
            var plan = Plan(item); var detail = view.Detail(uid, openEras)!;
            foreach (var part in J.A(detail, "parts").OfType<JsonObject>()) if (plan.Parts.FirstOrDefault(p => p.Uid == J.S(part, "uid")) is { } pp)
            { part["have"] = pp.Have; part["crafting"] = pp.Crafting; part["builds"] = pp.Builds; part["recipeUnknown"] = pp.RecipeUnknown; }
            row["item"] = detail;
            row["materials"] = J.Array(plan.Materials.Values.Select(n => new { uid = n.Uid, name = n.Name, count = n.Count, have = view.Inventory != null || view.Manual.Counts.ContainsKey(n.Uid) ? view.Count(n.Uid) : (long?)null, image = view.IconOf(n.Uid, n.Name), drops = view.Catalog.Drops.ForName(n.Name) }));
            row["credits"] = J.Node(new { need = plan.Credits, have = view.Inventory?.Currencies.Credits });
        }
        else
        {
            var held = Held(uid, thing.Kind); row["held"] = J.Node(new { count = held.Count, ranks = held.Ranks, byHand = held.ByHand }); row["target"] = task.Target ?? DefaultTarget(thing);
            row["targetDefault"] = DefaultTarget(thing); row["topRank"] = thing.TopRank;
            if (thing.Kind == "arcane") row["rank"] = Enumerable.Range(0, (int)Math.Clamp(thing.TopRank ?? 0, 0, 100) + 1).Where(r => ArcaneCopies(r) <= held.Count).Select(r => (int?)r).LastOrDefault();
            row["drops"] = J.Array(view.Catalog.Drops.ForName(thing.Name)); row["dropCoverage"] = view.Coverage;
        }
        return row;
    }
    public JsonArray Find(string query)
    {
        if (query.Trim().Length < 2) return [];
        var candidates = view.Catalog.Items.Keys.Concat(view.Catalog.Inventory.Keys).Concat(view.Catalog.Lookup.Keys).Concat(view.Catalog.Resources.Keys).Distinct()
            .Select(uid => (Uid: uid, Thing: Thing(uid))).Where(p => p.Thing != null && GameView.MatchRank(p.Thing.Name, query) != null);
        bool Variant(string uid) => new[] { "/Beginner/", "/Intermediate/", "/Expert/" }.Any(s => uid.Contains(s, StringComparison.Ordinal));
        return J.Array(candidates.OrderBy(p => Variant(p.Uid)).ThenBy(p => p.Uid, StringComparer.Ordinal).GroupBy(p => p.Thing!.Kind is "mod" or "arcane" ? p.Thing.Name.ToLowerInvariant() : p.Uid).Select(g => g.First())
            .OrderBy(p => GameView.MatchRank(p.Thing!.Name, query)).ThenBy(p => p.Thing!.Name.Length).ThenBy(p => p.Thing!.Name, StringComparer.Ordinal).Take(30)
            .Select(p => new { key = p.Uid, kind = p.Thing!.Kind, name = p.Thing.Name, sub = p.Thing.Sub, image = p.Thing.Image, added = view.Manual.Tasks.Any(t => t.Uid == p.Uid) }));
    }
}
