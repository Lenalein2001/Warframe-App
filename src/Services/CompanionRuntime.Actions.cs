using System.Text.Json.Nodes;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;

namespace WarframeCompanion.Services;

public sealed partial class CompanionRuntime
{
    private JsonNode RewardSelections(string? owner)
    {
        var history = History.Read(owner, Started);
        if (owner == null) return history;
        var rows = J.A(history, "entries").Where(e => J.S(e, "account") == owner).ToArray();
        var selected = snapshots.Picks(owner, rows.Select(e => J.S(e, "id")!).ToArray());
        foreach (var row in rows) row["picked"] = selected.GetValueOrDefault(J.S(row, "id")!);
        return history;
    }

    private JsonNode RecordInventoryAction(string path, JsonNode body)
    {
        var owner = Active;
        if (owner == null || J.S(body, "accountKey") != owner.Key)
            throw new InventoryException("The selected account changed. Reopen this action on its original account.");
        var identity = owner.Id ?? owner.Key;
        if (path == "rewards/pick")
        {
            var id = J.S(body, "id") ?? throw new InventoryException("Choose a reward screen.");
            var screen = History.Screen(owner.Key, id) ?? throw new InventoryException("Reward screen unavailable for this account.");
            if (body is not JsonObject fields || !fields.ContainsKey("uid") || body["uid"] != null && J.S(body, "uid") == null)
                throw new InventoryException("Choose a listed reward or clear the pick.");
            var uid = J.S(body, "uid");
            InventoryEffect[] effects = [];
            if (uid != null)
            {
                var drop = J.A(screen, "drops").FirstOrDefault(d => J.S(d, "uid") == uid)
                    ?? throw new InventoryException("This item was not offered on that screen.");
                effects = [new("stack", J.S(drop, "inventoryUid") ?? uid, 1)];
            }
            snapshots.SaveAction(owner.Key, identity, "pick:" + id, J.D(screen["t"])!.Value, uid, effects,
                checkPick: fields.ContainsKey("previous"), expectedPick: J.S(body, "previous"));
        }
        else
        {
            var uid = J.S(body, "uid") ?? throw new InventoryException("Choose an item to mark built.");
            var token = J.S(body, "token") ?? throw new InventoryException("Reopen the finished build.");
            if (!token.StartsWith("claim:", StringComparison.Ordinal) && !token.StartsWith("build:", StringComparison.Ordinal))
                throw new InventoryException("Invalid build reference.");
            if (snapshots.HasAction(owner.Key, token)) return J.Node(new { ok = true });
            var view = ViewOf(owner, false, out _);
            if (view.Baseline != J.N(body, "baseline", -1)) throw new InventoryException("Inventory changed. Reopen the build.");
            var (offer, effects) = view.BuildEffects(uid, token);
            if (offer == null) throw new InventoryException("This build is no longer available. Reopen the item.");
            snapshots.SaveAction(owner.Key, identity, token, J.Now, uid, effects, view.Baseline);
        }
        return J.Node(new { ok = true });
    }
}
