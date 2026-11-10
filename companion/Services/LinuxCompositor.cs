using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed record GameRectangle(int X, int Y, int Width, int Height);
public static class LinuxCompositor
{
    private static bool Game(string? name, string? title) => name is "steam_app_230410" or "Warframe.x64.exe" or "Warframe.exe" || string.Equals(title?.Trim(), "Warframe", StringComparison.OrdinalIgnoreCase);
    private static GameRectangle? Rect(long x, long y, long width, long height) => width is > 0 and <= 16384 && height is > 0 and <= 16384 && x is >= -100000 and <= 100000 && y is >= -100000 and <= 100000 ? new((int)x, (int)y, (int)width, (int)height) : null;
    public static GameRectangle? Hyprland(JsonNode clients)
    {
        foreach (var client in J.A(clients))
            if (Game(J.S(client, "class"), J.S(client, "title")) && client["at"] is JsonArray at && at.Count == 2 && client["size"] is JsonArray size && size.Count == 2 && Rect(J.N(at[0]), J.N(at[1]), J.N(size[0]), J.N(size[1])) is { } rectangle) return rectangle;
        return null;
    }
    public static GameRectangle? Sway(JsonNode tree, int depth = 0)
    {
        if (depth > 64) return null;
        if (Game(J.S(tree, "app_id") ?? J.S(tree["window_properties"], "class"), J.S(tree, "name")) && tree["rect"] is { } r && Rect(J.N(r, "x"), J.N(r, "y"), J.N(r, "width"), J.N(r, "height")) is { } rectangle) return rectangle;
        foreach (var child in J.A(tree, "nodes").Concat(J.A(tree, "floating_nodes"))) if (Sway(child, depth + 1) is { } found) return found;
        return null;
    }
    public static async Task<GameRectangle?> GameBounds(CancellationToken cancellation)
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")) && ScreenCapture.Which("hyprctl") is { } hypr)
                return Hyprland(JsonNode.Parse(await ScreenCapture.Run(hypr, ["clients", "-j"], cancellation, 5))!);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK")) && ScreenCapture.Which("swaymsg") is { } sway)
                return Sway(JsonNode.Parse(await ScreenCapture.Run(sway, ["-t", "get_tree"], cancellation, 5))!);
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or Core.InventoryException or System.Text.Json.JsonException or TaskCanceledException) { }
        return null;
    }
}
