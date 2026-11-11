using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;
using WarframeCompanion.Services;

namespace WarframeCompanion.Desktop;

internal sealed class NativeHotkeys(CompanionRuntime runtime, string url) : IDisposable
{
    private Thread? thread;
    private uint threadId;
    private string? error;
    private readonly CancellationTokenSource stop = new();
    public JsonNode Status()
    {
        var settings = runtime.GetSettings(); var lines = new[] { J.S(settings, "hotkey_rewards") + " → " + url + "/api/rewards/scan", J.S(settings, "hotkey_riven") + " → " + url + "/api/riven/scan" };
        return J.Node(new { compositor = OperatingSystem.IsWindows() ? "Windows" : "Wayland/X11", lines, note = error ?? (OperatingSystem.IsWindows() ? "Hotkeys are held by this desktop app." : "Install bindings in Hyprland or Sway; other desktops can bind the displayed HTTP commands.") });
    }
    public JsonNode Register()
    {
        if (!OperatingSystem.IsWindows()) return InstallLinux();
        StopThread(); error = null; using var ready = new ManualResetEventSlim(); var keys = runtime.GetSettings();
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId(); PeekMessage(out _, 0, 0, 0, 0);
            try
            {
                var names = new[] { J.S(keys, "hotkey_rewards"), J.S(keys, "hotkey_riven") };
                for (var i = 0; i < names.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(names[i])) continue;
                    var (mod, key) = Parse(names[i]!); if (!RegisterHotKey(0, i + 1, mod | 0x4000, key)) throw new InventoryException("A hotkey is already held by another application: " + names[i]);
                }
                ready.Set();
                while (GetMessage(out var message, 0, 0, 0) > 0)
                    if (message.Id == 0x312) _ = Invoke(message.WParam == 1);
            }
            catch (Exception e) { error = e.Message; ready.Set(); }
            finally { UnregisterHotKey(0, 1); UnregisterHotKey(0, 2); }
        }) { IsBackground = true, Name = "Warframe global hotkeys" };
        thread.Start(); ready.Wait(); if (error != null) throw new InventoryException(error);
        return J.Node(new { file = (string?)null, sourced = false, lines = Status()["lines"] });
    }
    private async Task Invoke(bool rewards)
    { try { if (rewards) await runtime.ScanRewards(false, stop.Token); else await runtime.ScanRiven(stop.Token); } catch (OperationCanceledException) { } catch (Exception e) { runtime.Event("Hotkey scan failed: " + e.GetType().Name, "error"); } }
    private static (uint Modifier, uint Key) Parse(string value)
    {
        var parts = value.ToUpperInvariant().Split('+'); uint mod = 0; foreach (var part in parts.SkipLast(1)) mod |= part switch { "ALT" => 1u, "CTRL" => 2u, "SHIFT" => 4u, "WIN" or "SUPER" => 8u, _ => throw new InventoryException("Unknown hotkey modifier.") };
        var key = parts[^1]; var code = key.StartsWith('F') && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24 ? (uint)(111 + number) : key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) ? key[0] : throw new InventoryException("Invalid hotkey."); return (mod, code);
    }
    private JsonNode InstallLinux()
    {
        var hypr = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") != null; var sway = Environment.GetEnvironmentVariable("SWAYSOCK") != null;
        if (!hypr && !sway) throw new InventoryException("No supported compositor detected. Bind the displayed commands in your desktop's shortcut settings.");
        var lua = hypr && ScreenCapture.Run("hyprctl", ["systeminfo"], stop.Token, 5).GetAwaiter().GetResult().Contains("configProvider: lua", StringComparison.Ordinal);
        var flavour = hypr ? lua ? "hyprland-lua" : "hyprland" : "sway";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); var dir = Path.Combine(home, ".config", hypr ? "hypr" : "sway"); var main = Path.Combine(dir, lua ? "lua/keybindings.lua" : hypr ? "hyprland.conf" : "config");
        if (!File.Exists(main)) throw new InventoryException("Compositor configuration was not found.");
        var config = runtime.GetSettings(); var pairs = new[] { (J.S(config, "hotkey_rewards"), "rewards/scan"), (J.S(config, "hotkey_riven"), "riven/scan") };
        var authFile = WarframeCompanion.Host.LocalApiCredential.PathFor(runtime.DataDirectory, new Uri(url).Port);
        if (authFile.Any(c => c is '\n' or '\r' or '#' or '$' or '"' or '\\' or '\'')) throw new InventoryException("Hotkey configuration requires a data path without shell metacharacters.");
        var lines = pairs.Where(p => !string.IsNullOrWhiteSpace(p.Item1)).Select(p => CompositorBindings.Line(flavour, p.Item1!, "curl -s -H @'" + authFile + "' -X POST " + url + "/api/" + p.Item2)).ToArray();
        if (lua)
        {
            var merged = CompositorBindings.MergeLua(File.ReadAllText(main), lines);
            File.Copy(main, main + ".warframe-backup-" + Guid.NewGuid().ToString("N"), false);
            Atomic(main, merged.Text);
            ReloadLinux("hyprctl");
            return J.Node(new { file = main, sourced = merged.Added, lines });
        }
        var target = Path.Combine(dir, "warframe-companion.conf"); Atomic(target, "# Managed by Warframe Companion\n" + string.Join('\n', lines) + "\n");
        var text = File.ReadAllText(main); var include = (hypr ? "source = " : "include ") + target; var added = !text.Contains(include, StringComparison.Ordinal);
        if (added) { File.Copy(main, main + ".warframe-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false); Atomic(main, text.TrimEnd() + "\n\n" + include + "\n"); }
        ReloadLinux(hypr ? "hyprctl" : "swaymsg");
        return J.Node(new { file = target, sourced = added, lines });
    }
    private void ReloadLinux(string program)
    {
        var result = ScreenCapture.Run(program, ["reload"], stop.Token, 15).GetAwaiter().GetResult();
        if (result.Contains("error", StringComparison.OrdinalIgnoreCase)) throw new InventoryException("Compositor reload reported an error. The previous configuration was backed up.");
    }
    private static void Atomic(string path, string content) { var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; try { File.WriteAllText(tmp, content); File.Move(tmp, path, true); } finally { if (File.Exists(tmp)) File.Delete(tmp); } }
    private void StopThread() { if (thread is { IsAlive: true }) { PostThreadMessage(threadId, 0x12, 0, 0); thread.Join(TimeSpan.FromSeconds(5)); } thread = null; }
    public void Dispose() { stop.Cancel(); StopThread(); stop.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
}
