using System.Text.Json.Nodes;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed partial class GameView
{
    private sealed record Reservation(string Uid, string Name, decimal Count);

    private Dictionary<string, List<Reservation>> CraftReservations(out bool complete)
    {
        var result = new Dictionary<string, List<Reservation>>();
        var known = true;
        complete = true;
        if (Manual.Tasks.Count == 0) return result;
        var recipes = Catalog.Recipes.ToLookup(p => p.Value.Result);
        var remaining = new Dictionary<string, decimal>();
        var pending = (Inventory?.Foundry ?? []).ToLookup(f => f.Recipe);
        var planner = new FarmingPlanner(this);
        foreach (var task in Manual.Tasks.DistinctBy(t => t.Uid))
        {
            if (!Catalog.Items.TryGetValue(task.Uid, out var item))
            {
                if (planner.Thing(task.Uid) == null) known = false;
                continue;
            }
            var options = recipes[item.Uid].ToArray();
            if (Owned(item.Uid) > 0 || Status(item).Building || options.Any(p => pending.Contains(p.Key))) continue;
            var needs = new Dictionary<string, decimal>();
            var ancestors = new HashSet<string> { item.Uid };
            var steps = 0;
            void Reserve(string uid, decimal count)
            {
                // A malformed external recipe graph must not exhaust the stack or stall Inventory.
                if (++steps > 8192 || ancestors.Count >= 32 || count > long.MaxValue) { known = false; return; }
                needs[uid] = needs.GetValueOrDefault(uid) + count;
                var choices = recipes[uid].ToArray();
                if (choices.Length == 0)
                {
                    if (count > Quantity(Inventory, uid) && (Catalog.Items.GetValueOrDefault(uid)?.Components.Count > 0 ||
                        uid.EndsWith("Component", StringComparison.Ordinal) && Catalog.Aliases.ContainsKey(uid))) known = false;
                    return;
                }
                if (!remaining.TryGetValue(uid, out var available))
                {
                    var inFoundry = choices.Sum(p => (decimal)pending[p.Key].Count() * p.Value.Quantity);
                    available = Math.Max(0, Quantity(Inventory, uid) + inFoundry - Manual.KeepCopies.GetValueOrDefault(uid));
                }
                var shortage = Math.Max(0, count - available);
                remaining[uid] = Math.Max(0, available - count);
                if (shortage == 0) return;
                if (choices.Length != 1 || !ancestors.Add(uid))
                {
                    known = false;
                    return;
                }
                var blueprint = choices[0];
                var recipe = blueprint.Value;
                var builds = decimal.Ceiling(shortage / recipe.Quantity);
                needs[blueprint.Key] = needs.GetValueOrDefault(blueprint.Key) + builds;
                remaining[uid] += builds * recipe.Quantity - shortage;
                foreach (var ingredient in recipe.Ingredients)
                {
                    if (builds > long.MaxValue / ingredient.Count) known = false;
                    else Reserve(ingredient.Uid, builds * ingredient.Count);
                }
                ancestors.Remove(uid);
            }
            if (options.Length == 1)
            {
                needs[options[0].Key] = 1;
                foreach (var ingredient in options[0].Value.Ingredients) Reserve(ingredient.Uid, ingredient.Count);
            }
            else
            {
                known = false;
                foreach (var component in item.Components) Reserve(component.Uid, component.Count);
            }
            foreach (var (uid, count) in needs)
            {
                if (!result.TryGetValue(uid, out var goals)) result[uid] = goals = [];
                goals.Add(new(item.Uid, item.Name, count));
            }
        }
        complete = known;
        return result;
    }

    public JsonNode CopiesToKeep(string? accountKey)
    {
        var reservations = CraftReservations(out var complete);
        var entries = Inventory == null ? Enumerable.Empty<string>() : Inventory.Equipment.Keys
            .Concat(Inventory.Counts.Where(p => p.Value > 0).Select(p => p.Key)).Concat(Inventory.Upgrades?.Keys.AsEnumerable() ?? []);
        object Copies(string uid)
        {
            var goals = reservations.GetValueOrDefault(uid) ?? [];
            var minimum = Manual.KeepCopies.GetValueOrDefault(uid);
            var uses = goals.Sum(g => g.Count);
            decimal? required = uses;
            if (uses > 1 && Catalog.Recipes.TryGetValue(uid, out var recipe))
                required = recipe.ConsumeOnUse switch { true => uses, false => 1, null => null };
            var item = Catalog.Items.GetValueOrDefault(uid);
            var owned = Inventory == null || RankedQuantityUnknown(Inventory, uid) ? (decimal?)null : Quantity(Inventory, uid);
            if (owned == 0 && Inventory?.OptionalQuantitySections is { } sections)
            {
                var section = item?.Category switch
                {
                    "Primary" => "LongGuns", "Secondary" => "Pistols", "Melee" => "Melee", "Archwing" => "SpaceSuits",
                    "Arch-Gun" => "SpaceGuns", "Arch-Melee" => "SpaceMelee", "Sentinels" => "Sentinels",
                    "SentinelWeapons" => "SentinelWeapons", _ => null
                };
                if (section != null && !sections.ContainsKey(section)) owned = null;
            }
            var masteredWeapon = IsWeapon(item) && Status(item!).Status == "mastered";
            return new { uid, name = InventoryEntry(uid).Name, minimum, required, owned, goals, masteredWeapon,
                spare = owned.HasValue && complete && required.HasValue ? (decimal?)Math.Max(0, owned.Value - required.Value - minimum) : null };
        }
        var copies = entries.Union(Manual.KeepCopies.Keys).Union(reservations.Keys).Select(Copies);
        return J.Node(new { accountKey, copies, complete, hasInventory = Inventory != null });
    }

    private static decimal Quantity(InventorySnapshot? snapshot, string uid) =>
        (decimal)(snapshot?.Counts.GetValueOrDefault(uid) ?? 0) + (snapshot?.Equipment.GetValueOrDefault(uid)?.Count ?? 0)
        + (snapshot?.Upgrades?.GetValueOrDefault(uid)?.Count ?? 0);

    private bool RankedQuantityUnknown(InventorySnapshot snapshot, string uid, InventorySnapshot? other = null) =>
        snapshot.Upgrades == null && (other?.Upgrades?.ContainsKey(uid) == true ||
            snapshot.OptionalQuantitySections?.GetValueOrDefault("RawUpgrades")?.Contains(uid) == true ||
            Catalog.Inventory.GetValueOrDefault(uid)?.Kind is "Mods" or "Arcanes" || Catalog.Lookup.GetValueOrDefault(uid)?.Category == "Mods"
            || uid.Contains("/Upgrades/Mods/", StringComparison.Ordinal));

    private static double? SyncTime(InventorySnapshot? inventory) =>
        inventory?.SourceTimestamp ?? inventory?.ReadAt.ToUnixTimeMilliseconds() / 1000.0;

    private static HashSet<string> MissingQuantities(InventorySnapshot before, InventorySnapshot after)
    {
        var missing = new HashSet<string>();
        if (before.OptionalQuantitySections is not { } oldSections || after.OptionalQuantitySections is not { } newSections) return missing;
        foreach (var (key, members) in oldSections)
            if (!newSections.ContainsKey(key)) missing.UnionWith(members);
        foreach (var (key, members) in newSections)
            if (!oldSections.ContainsKey(key)) missing.UnionWith(members);
        return missing;
    }

    public JsonNode InventoryChanges(InventorySnapshot? previous)
    {
        var current = Inventory;
        var items = new List<object>();
        var currencies = new List<object>();
        var goals = new List<object>();
        if (previous?.AccountId != current?.AccountId) previous = null;
        var limited = false;
        if (current != null && previous != null)
        {
            var missing = MissingQuantities(previous, current);
            limited = missing.Count > 0 || previous.OptionalQuantitySections == null || current.OptionalQuantitySections == null;
            var ids = previous.Counts.Keys.Concat(current.Counts.Keys).Concat(previous.Equipment.Keys).Concat(current.Equipment.Keys)
                .Concat(previous.Upgrades?.Keys.AsEnumerable() ?? []).Concat(current.Upgrades?.Keys.AsEnumerable() ?? []).Distinct();
            var earlier = new GameView(Catalog, previous, null, Manual, Market);
            var related = earlier.CraftReservations(out _).Concat(CraftReservations(out _))
                .SelectMany(p => p.Value.Select(goal => (p.Key, goal)))
                .Concat(Manual.Tasks.Select(t => (t.Uid, new Reservation(t.Uid, Catalog.NameOf(t.Uid), 0))))
                .ToLookup(p => p.Item1, p => p.Item2);
            foreach (var uid in ids)
            {
                var before = Quantity(previous, uid);
                var after = Quantity(current, uid);
                if (before == after) continue;
                if (RankedQuantityUnknown(previous, uid, current) || RankedQuantityUnknown(current, uid, previous)) missing.Add(uid);
                if (missing.Contains(uid)) { limited = true; continue; }
                items.Add(new { uid, name = InventoryEntry(uid).Name, before, after, delta = after - before,
                    goals = related[uid].DistinctBy(r => r.Uid).Select(r => new { r.Uid, r.Name }) });
            }
            void Currency(string name, long? before, long? after)
            {
                if (before.HasValue && after.HasValue && before != after)
                    currencies.Add(new { name, before, after, delta = (decimal)after.Value - before.Value });
            }
            Currency("Credits", previous.Currencies.Credits, current.Currencies.Credits);
            Currency("Platinum", previous.Currencies.Platinum, current.Currencies.Platinum);
            Currency("Endo", previous.Currencies.Endo, current.Currencies.Endo);
            Currency("Ducats", previous.Currencies.Ducats, current.Currencies.Ducats);
            // Evaluate today's board against both snapshots without applying today's manual counts.
            FarmingPlanner Planner(InventorySnapshot snapshot) => new(new(Catalog, snapshot, null, new(), Market));
            var oldPlan = Planner(previous); var newPlan = Planner(current);
            var uncertainGoals = missing.SelectMany(uid => related[uid]).Select(r => r.Uid).ToHashSet();
            foreach (var task in Manual.Tasks)
            {
                if (uncertainGoals.Contains(task.Uid) || RankedQuantityUnknown(previous, task.Uid, current) || RankedQuantityUnknown(current, task.Uid, previous)) continue;
                var before = oldPlan.Row(task); var after = newPlan.Row(task);
                if (!JsonNode.DeepEquals(before, after))
                    goals.Add(new { uid = task.Uid, name = J.S(after, "name"), before = J.S(before, "note"), after = J.S(after, "note"),
                        beforeHave = J.N(before, "have"), afterHave = J.N(after, "have") });
            }
        }
        return J.Node(new { from = SyncTime(previous), to = SyncTime(current), items, currencies, goals, limited });
    }
}
