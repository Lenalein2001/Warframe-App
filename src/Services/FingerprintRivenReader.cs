using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

/// <summary>Reads explicitly supplied game riven data. Never guesses which owned mod is on screen.</summary>
public static class FingerprintRivenReader
{
    public static JsonNode Read(Catalog catalog, JsonNode body)
    {
        var uid = J.S(body, "ItemType") ?? throw new InventoryException("Riven ItemType is required.");
        if (!uid.StartsWith("/Lotus/Upgrades/Mods/Randomized/", StringComparison.Ordinal) || catalog.UpgradeExport?[uid] is not JsonObject definition) throw new InventoryException("No current export definition for this riven type.");
        var fingerprint = body["UpgradeFingerprint"];
        if (fingerprint is JsonValue v && v.TryGetValue<string>(out var encoded))
        {
            if (encoded.Length > 32768) throw new InventoryException("Riven fingerprint exceeds the size limit.");
            try { fingerprint = JsonNode.Parse(encoded); } catch (System.Text.Json.JsonException) { throw new InventoryException("Invalid riven fingerprint JSON."); }
        }
        if (fingerprint is not JsonObject fp || fp["buffs"] is not JsonArray buffs || buffs.Count is < 2 or > 3 || fp["curses"] is not (null or JsonArray)) throw new InventoryException("An unveiled riven with 2–3 buffs is required.");
        var curses = fp["curses"] as JsonArray ?? []; if (curses.Count > 1) throw new InventoryException("Unsupported riven curse count.");
        var weaponId = J.S(fp, "compat") ?? "";
        if (!catalog.Items.TryGetValue(weaponId, out var weapon) || weapon.Disposition is not > 0) throw new InventoryException("The riven's exact weapon variant is not in the catalog.");
        var rank = J.N(fp, "lvl", -1); var maximum = J.N(definition, "fusionLimit", 8); if (rank < 0 || rank > maximum) throw new InventoryException("Invalid riven mod rank.");
        var drain = J.N(definition, "baseDrain", 10); var rows = new List<JsonNode>(); var seen = new HashSet<string>();
        foreach (var (entries, negative) in new[] { (buffs, false), (curses, true) }) foreach (var entry in entries)
        {
            var tag = J.S(entry, "Tag") ?? ""; var roll = J.N(entry, "Value", -1);
            if (roll is < 0 or > 0x3fffffff || !seen.Add(tag)) throw new InventoryException("Invalid or repeated riven trait.");
            var rule = J.A(definition, "upgradeEntries").FirstOrDefault(r => J.S(r, "tag") == tag) ?? throw new InventoryException("Unknown riven trait; update the game catalog.");
            if (!J.B(rule[negative ? "canBeCurse" : "canBeBuff"])) throw new InventoryException("Riven trait has an invalid sign.");
            var fraction = roll / (double)0x3fffffff;
            var multiplier = negative ? buffs.Count == 2 ? .33000001 : .5 : (buffs.Count == 2 ? .66000003 : .5) * (curses.Count == 1 ? 1.25 : 1);
            var factor = 1.5 * weapon.Disposition.Value * drain * (rank + 1) * multiplier;
            var values = J.A(rule, "upgradeValues").Where(r => J.S(r, "locTag") != null).ToArray();
            if (values.Length == 0) throw new InventoryException("The export has no display definition for a riven trait.");
            foreach (var value in values)
            {
                var basis = J.D(value["value"]) ?? throw new InventoryException("The export has no numeric value for a riven trait.");
                var absolute = tag is "ComboDurationMod" or "WeaponMeleeRangeIncMod" or "WeaponMeleeComboInitialBonusMod" or "WeaponPunctureDepthMod";
                var centre = basis * factor * (absolute ? 1 : 100) * (negative ? -1 : 1);
                var rolled = centre * (.9 + .2 * fraction); var deviation = (-10 + 20 * fraction) * (negative ? -1 : 1);
                var text = J.S(catalog.Dictionary?[J.S(value, "locTag") ?? ""]) ?? tag;
                var label = Regex.Replace(CatalogLoader.Clean(text), @"[+\-]?\s*\{[^}]*\}|<[^>]*>|\bVALUE\b", "").Trim(' ', '+', '-', '%');
                rows.Add(J.Node(new { stat = label.Length == 0 ? tag : label, tag, value = Math.Round(rolled, 1), centre = Math.Round(centre, 3), deviation = Math.Round(deviation, 3), grade = RivenService.Grade(deviation), negative, outOfRange = false, signConflict = false }));
            }
        }
        return J.Node(new { t = J.Now, source = "game fingerprint", weapon = weapon.Name, image = Catalog.ImageUrl(weapon.Image), rank, positives = buffs.Count, negatives = curses.Count, unread = 0,
            stats = rows, overall = RivenService.Grade(rows.Average(r => J.D(r["deviation"]) ?? 0)), disposition = weapon.Disposition, traded = (object?)null, market = (object?)null, error = (string?)null });
    }
}
