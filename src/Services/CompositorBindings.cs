using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public static class CompositorBindings
{
    public static string Line(string compositor, string key, string command)
    {
        var parts = key.Split('+');
        var modifiers = parts.SkipLast(1).Select(p => p.ToUpperInvariant() switch
        {
            "WIN" or "SUPER" => compositor == "sway" ? "Mod4" : "SUPER",
            "CTRL" => compositor == "sway" ? "Ctrl" : "CTRL",
            "ALT" => compositor == "sway" ? "Mod1" : "ALT",
            "SHIFT" => compositor == "sway" ? "Shift" : "SHIFT",
            _ => throw new InventoryException("Unknown hotkey modifier.")
        }).ToArray();
        return compositor switch
        {
            "hyprland-lua" => "hl.bind(\"" + string.Join(" + ", modifiers.Append(parts[^1])) + "\", hl.dsp.exec_cmd(\"" + command + "\"))",
            "hyprland" => "bind = " + string.Join(' ', modifiers) + ", " + parts[^1] + ", exec, " + command,
            "sway" => "bindsym " + string.Join('+', modifiers.Append(parts[^1])) + " exec " + command,
            _ => throw new InventoryException("Unknown compositor.")
        };
    }

    public static (string Text, bool Added) MergeLua(string original, IEnumerable<string> lines)
    {
        // Preserve the old application's block markers so migration cannot duplicate bindings.
        const string head = "-- >>> warframeproject hotkeys (managed)";
        const string tail = "-- <<< warframeproject hotkeys";
        var start = original.IndexOf(head, StringComparison.Ordinal);
        var end = original.IndexOf(tail, StringComparison.Ordinal);
        var block = head + "\n" + string.Join('\n', lines) + "\n" + tail;
        if (start < 0 && end < 0) return (original.TrimEnd() + "\n\n" + block + "\n", true);
        if (start < 0 || end < start || original.IndexOf(head, start + head.Length, StringComparison.Ordinal) >= 0 || original.IndexOf(tail, end + tail.Length, StringComparison.Ordinal) >= 0)
            throw new InventoryException("The managed hotkey block is incomplete or duplicated. Repair it before reinstalling bindings.");
        return (original[..start] + block + original[(end + tail.Length)..], false);
    }
}
