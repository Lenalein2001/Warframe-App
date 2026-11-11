using System.Drawing;
using Photino.NET;
using WarframeCompanion.Host;

namespace WarframeCompanion.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var timing = new StartupTiming();
        string? dataDirectory = null;
        try
        {
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine("WarframeCompanion.Desktop [run] [--offline] [--headless | --browser] [--portable | --data-dir PATH] [--no-game] [--port NUMBER]\nWarframeCompanion.Desktop trigger|riven|quit [--port NUMBER] [--data-dir PATH]\nWarframeCompanion.Desktop replay IMAGE... [--riven] [--scale PERCENT] [--offline] [--data-dir PATH] [--output JSONL]");
                return 0;
            }
            if (DesktopCommands.Execute(args).GetAwaiter().GetResult() is { } commandResult) return commandResult;
            if (args.FirstOrDefault() == "run") args = args[1..];
            var options = new Dictionary<string, string?>();
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (options.ContainsKey(key)) throw new ArgumentException("Duplicate option.");
                if (key is "--offline" or "--portable" or "--headless" or "--browser" or "--no-game" or "--migrate-legacy") options[key] = null;
                else if (key is "--data-dir" or "--port" && i + 1 < args.Length) options[key] = args[++i];
                else throw new ArgumentException("Usage: WarframeCompanion.Desktop [--portable | --data-dir PATH] [--offline] [--headless] [--no-game] [--migrate-legacy] [--port NUMBER]");
            }
            if (options.ContainsKey("--portable") && options.ContainsKey("--data-dir")) throw new ArgumentException("Choose one data location.");
            var directory = options.GetValueOrDefault("--data-dir") ?? (options.ContainsKey("--portable") ? Path.Combine(AppContext.BaseDirectory, "data") :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarframeCompanion"));
            dataDirectory = Path.GetFullPath(directory);
            var port = options.GetValueOrDefault("--port") is { } p ? int.Parse(p) : (int?)null;
            var interactive = !options.ContainsKey("--headless") && !options.ContainsKey("--browser");
            timing.Mark("options-parsed");
            CompanionServer server;
            try { server = CompanionServer.Start(directory, options.ContainsKey("--offline"), port, migrateLegacy: options.ContainsKey("--migrate-legacy") || !options.ContainsKey("--portable") && !options.ContainsKey("--data-dir"), watchGame: !options.ContainsKey("--no-game"), deferRuntimeStart: interactive).GetAwaiter().GetResult(); }
            catch (ProfileInUseException) when (!options.ContainsKey("--headless") && !options.ContainsKey("--browser"))
            {
                if (ProfileInstance.TryShow(directory, port).GetAwaiter().GetResult()) return 0;
                throw;
            }
            timing.Mark("local-server-ready");
            timing.Attach(directory);
            try
            {
                if (options.ContainsKey("--browser")) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(server.LaunchUrl) { UseShellExecute = true });
                if (options.ContainsKey("--headless") || options.ContainsKey("--browser")) { server.WaitForShutdown().GetAwaiter().GetResult(); return 0; }
                timing.Mark("window-configuration-start");
                var window = new PhotinoWindow { LogVerbosity = 0 }.SetIconFile(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "icon.ico" : "icon.png")).SetTitle("Warframe Companion").SetUseOsDefaultSize(false).SetSize(new Size(1280, 860)).Center()
                    .SetContextMenuEnabled(false).SetDevToolsEnabled(false).SetTemporaryFilesPath(Path.Combine(directory, "webview")).Load(server.LaunchUrl);
                timing.Mark("window-configured");
                window.RegisterWindowCreatedHandler((_, _) =>
                {
                    timing.Mark("native-window-created");
                    server.Runtime.Start(!options.ContainsKey("--no-game"));
                    timing.Mark("background-started");
                });
                window.RegisterWebMessageReceivedHandler((_, message) =>
                {
                    if (message == "companion-ui-ready") timing.Mark("ui-script-ready");
                });
                using var overlay = new NativeOverlay(window, server.Url, Path.GetFullPath(directory), message => server.Runtime.Event(message, "error"));
                using var hotkeys = new NativeHotkeys(server.Runtime, server.Url);
                server.Runtime.OverlayRequested += overlay.Show; server.Runtime.OverlayHidden += overlay.Hide;
                server.Runtime.OverlayUpdated += overlay.Update;
                server.Runtime.RegisterHotkeys = hotkeys.Register; server.Runtime.HotkeyStatus = hotkeys.Status;
                using var notifications = new NativeNotifications();
                server.Runtime.NotificationRequested += notifications.Show;
                timing.Mark("hotkeys-start");
                if (OperatingSystem.IsWindows()) { try { hotkeys.Register(); } catch (Exception e) { server.Runtime.Event(e.Message, "error"); } }
                timing.Mark("hotkeys-ready");
                server.Runtime.Desktop = true;
                server.Runtime.ShowRequested += () => window.Invoke(() => { window.SetMinimized(false); window.SetTopMost(true); window.SetTopMost(false); });
                server.Runtime.QuitRequested += () => window.Invoke(window.Close);
                timing.Mark("enter-native-window-creation");
                window.WaitForClose();
            }
            finally { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return 0;
        }
        catch (Exception e)
        {
            // Startup diagnostics never contain payloads or account identifiers.
            StartupFailure.Report(e, dataDirectory, !args.Contains("--headless") && !args.Contains("--browser") &&
                !args.Any(a => a is "quit" or "trigger" or "riven" or "replay"));
            if (Environment.GetEnvironmentVariable("WFC_STARTUP_DIAGNOSTICS") == "1") Console.Error.WriteLine(e);
            return 1;
        }
    }
}
