using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public static class Settings
{
    public static JsonObject Defaults() => (JsonObject)JsonNode.Parse("""
        {"port":8642,"platform":"pc","ee_log_path":"","profile_auto_refresh":false,"profile_refresh_minutes":30,
         "inventory_json_path":"","hotkey_rewards":"F9","hotkey_riven":"F10","riven_overlay_enabled":true,"riven_overlay_seconds":20,
         "memory_scan_enabled":false,"ocr_enabled":true,"screenshot_tool":"auto","notify":false,"notify_fissures":false,
         "fissure_watch":[],"notify_baro":false,"notify_foundry":false,"overlay_enabled":true,"overlay_seconds":15,
         "overlay_position":"below","overlay_backend":"auto","price_cache_minutes":360}
        """)!;
    public static JsonObject Patch(JsonObject current, JsonNode patch)
    {
        if (patch is not JsonObject fields) throw new InventoryException("Settings must be an object.");
        var updated = (JsonObject)current.DeepClone(); var defaults = Defaults();
        foreach (var (key, value) in fields)
        {
            if (!defaults.ContainsKey(key)) continue;
            var expected = defaults[key]!.GetValueKind();
            var boolean = expected is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
            if (value == null || (boolean ? value.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) : value.GetValueKind() != expected)) throw new InventoryException("That setting has the wrong type: " + key);
            if (value is JsonValue number && value.GetValueKind() == System.Text.Json.JsonValueKind.Number && (!number.TryGetValue<long>(out var integer) || integer < 0 || integer > uint.MaxValue)) throw new InventoryException("Setting must be a nonnegative integer: " + key);
            updated[key] = value.DeepClone();
        }
        foreach (var key in new[] { "hotkey_rewards", "hotkey_riven" })
            if (J.S(updated, key) is { } hotkey && !Regex.IsMatch(hotkey, @"^(?:(?:Ctrl|Alt|Shift|Super|Win)\+)*(?:F(?:[1-9]|1[0-9]|2[0-4])|[A-Za-z0-9])$", RegexOptions.IgnoreCase)) throw new InventoryException("Use a letter, digit or F1–F24 hotkey with optional Ctrl/Alt/Shift/Win modifiers, or leave it blank to disable it.");
        if (J.N(updated, "port") is < 1024 or > 65535) throw new InventoryException("Choose a port from 1024 to 65535.");
        if (!new[] { "pc", "ps4", "xb1", "swi" }.Contains(J.S(updated, "platform"))) throw new InventoryException("Unknown platform.");
        if (!new[] { "below", "above" }.Contains(J.S(updated, "overlay_position"))) throw new InventoryException("Unknown overlay position.");
        if (!new[] { "auto", "layer-shell", "x11" }.Contains(J.S(updated, "overlay_backend"))) throw new InventoryException("Unknown overlay backend.");
        if (!new[] { "auto", "grim", "spectacle", "gnome-screenshot", "maim", "scrot", "import" }.Contains(J.S(updated, "screenshot_tool"))) throw new InventoryException("Unknown screenshot tool.");
        if (J.A(updated, "fissure_watch").Any(x => J.S(x) == null)) throw new InventoryException("Fissure watches must be strings.");
        return updated;
    }
}
