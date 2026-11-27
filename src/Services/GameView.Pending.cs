using System.Globalization;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;

namespace WarframeCompanion.Services;

internal sealed record BuildMark(string AccountKey, string Uid, long Baseline, string Token, bool Pending);

public sealed partial class GameView
{
    internal string? Owner { get; init; }
    internal long Baseline { get; init; }
    internal FoundryJob[] ConfirmedJobs { get; init; } = [];
    private Dictionary<FoundryJob, int>? jobSlots;
    internal IReadOnlyList<InventoryAction> Pending { get; init; } = [];
    internal JsonNode? CaveRoll { get; init; }
    internal double? SpiralStart { get; init; }
    internal double? SpiralEnd { get; init; }

    // Apply only to the private snapshot and manual state loaded for this view.
    internal static InventorySnapshot? ApplyPending(WorkingInventory data, ManualState manual)
    {
        var inventory = data.Snapshot;
        if (inventory == null || data.Pending.Count == 0) return inventory;
        var jobs = inventory.Foundry.ToList();
        var credits = inventory.Currencies.Credits;
        foreach (var action in data.Pending)
        foreach (var effect in action.Effects)
        {
            long Updated(long n) => (long)Math.Clamp((decimal)n + effect.Change, 0, long.MaxValue);
            var uid = effect.Uid;
            switch (effect.Kind)
            {
                case "claim":
                    var prefix = FormattableString.Invariant($"claim:{data.Baseline}:");
                    var index = action.Token.StartsWith(prefix, StringComparison.Ordinal) &&
                        int.TryParse(action.Token.AsSpan(prefix.Length), CultureInfo.InvariantCulture, out var slot) &&
                        slot >= 0 && slot < inventory.Foundry.Length
                        ? jobs.FindIndex(j => ReferenceEquals(j, inventory.Foundry[slot]))
                        : jobs.FindIndex(j => effect.InstanceId != null ? j.InstanceId == effect.InstanceId
                            : j.InstanceId == null && j.Recipe == uid && j.Done == effect.Done);
                    if (index >= 0) jobs.RemoveAt(index);
                    break;
                case "credits":
                    if (credits.HasValue) credits = Updated(credits.Value);
                    break;
                case "equipment":
                    var equipment = inventory.Equipment.GetValueOrDefault(uid) ?? new Equipment(0, 0);
                    var count = Updated(equipment.Count);
                    if (count == 0) inventory.Equipment.Remove(uid);
                    else inventory.Equipment[uid] = equipment with { Count = count };
                    break;
                case "stack":
                    inventory.Counts[uid] = Updated(inventory.Counts.GetValueOrDefault(uid));
                    if (manual.Counts.TryGetValue(uid, out var hand) && hand.Time < action.Time)
                        manual.Counts[uid] = hand with { Count = Updated(hand.Count) };
                    break;
            }
        }
        return inventory with { Foundry = jobs.ToArray(), Currencies = inventory.Currencies with { Credits = credits } };
    }

    private string? Output(FoundryJob job) => Catalog.Recipes.GetValueOrDefault(job.Recipe)?.Result ??
        Catalog.Parts.GetValueOrDefault(job.Recipe)?.Items.FirstOrDefault(uid =>
            Catalog.Items.TryGetValue(uid, out var item) && item.Components.Any(c => c.Uid == job.Recipe && c.Name == item.Name + " Blueprint"));

    internal BuildMark? BuildOffer(string uid, FoundryJob? job = null, bool another = false)
    {
        if (Owner == null || Inventory == null) return null;
        job ??= Inventory.Foundry.Where(j => Output(j) == uid).OrderBy(j => j.Done ?? double.MaxValue).FirstOrDefault();
        if (job != null)
        {
            if (jobSlots == null)
            {
                jobSlots = new(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < ConfirmedJobs.Length; i++) jobSlots[ConfirmedJobs[i]] = i;
            }
            return new(Owner, uid, Baseline, FormattableString.Invariant($"claim:{Baseline}:{jobSlots[job]}"), false);
        }
        if (!another && Pending.FirstOrDefault(a => a.Uid == uid && !a.Token.StartsWith("pick:", StringComparison.Ordinal)) is { } marked)
            return new(Owner, uid, Baseline, marked.Token, true);
        if (!Catalog.Items.TryGetValue(uid, out var gear) || gear.Components.Count == 0 && !Catalog.Recipes.Values.Any(r => r.Result == uid)) return null;
        var sequence = Pending.Count(a => a.Uid == uid && a.Token.StartsWith("build:", StringComparison.Ordinal));
        return new(Owner, uid, Baseline, FormattableString.Invariant($"build:{Baseline}:{sequence}:{uid}"), false);
    }

    internal (BuildMark? Offer, InventoryEffect[] Effects) BuildEffects(string uid, string token)
    {
        var job = Inventory?.Foundry.FirstOrDefault(j => Output(j) == uid && BuildOffer(uid, j)?.Token == token);
        var offer = BuildOffer(uid, job, another: true);
        if (offer == null || offer.Pending || offer.Token != token) return (null, []);
        var blueprint = job?.Recipe;
        if (job == null)
        {
            var recipes = Catalog.Recipes.Where(r => r.Value.Result == uid).Take(2).ToArray();
            if (recipes.Length == 1) blueprint = recipes[0].Key;
        }
        var recipe = blueprint != null ? Catalog.Recipes.GetValueOrDefault(blueprint) : null;
        var effects = new List<InventoryEffect> { new(Catalog.Items.ContainsKey(uid) ? "equipment" : "stack", uid, recipe?.Quantity ?? 1) };
        if (job != null)
            effects.Add(new("claim", job.Recipe, -1, job.Done, job.InstanceId));
        else
        {
            var item = Catalog.Items[uid];
            void Consume(string ingredient, long amount)
            {
                var equipment = Catalog.Items.ContainsKey(ingredient);
                var held = equipment ? Owned(ingredient) ?? 0 : HandCount(ingredient) ?? HeldStack(ingredient);
                if (held > 0) effects.Add(new(equipment ? "equipment" : "stack", ingredient, -Math.Min(held, amount)));
            }
            if (recipe != null)
            {
                foreach (var ingredients in recipe.Ingredients.GroupBy(i => i.Uid)) Consume(ingredients.Key, ingredients.Sum(i => i.Count));
                if (recipe.ConsumeOnUse == true && !recipe.Ingredients.Any(i => i.Uid == blueprint)) Consume(blueprint!, 1);
                if (recipe.Credits is > 0) effects.Add(new("credits", "", -recipe.Credits.Value));
            }
            else
            {
                foreach (var component in item.Components)
                {
                    if (component.Name == item.Name + " Blueprint") continue;
                    var alt = Catalog.Parts.GetValueOrDefault(component.Uid)?.Alt;
                    if (alt != null && alt.EndsWith("Component", StringComparison.Ordinal))
                    {
                        var take = Math.Min(component.Count, HeldStack(alt));
                        if (take > 0) Consume(alt, take);
                        if (component.Count > take) Consume(component.Uid, component.Count - take);
                    }
                    else Consume(component.Uid, component.Count);
                }
            }
        }
        return (offer, effects.ToArray());
    }
}
