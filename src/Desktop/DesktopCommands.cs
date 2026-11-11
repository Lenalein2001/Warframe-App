using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WarframeCompanion.Services;

namespace WarframeCompanion.Desktop;

internal static class DesktopCommands
{
    public static async Task<int?> Execute(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("replay" or "trigger" or "riven" or "quit")) return null;
        var command = args[0];
        var options = new Dictionary<string, string?>();
        var frames = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                if (command != "replay") throw new ArgumentException("Unexpected argument.");
                frames.Add(Path.GetFullPath(key)); continue;
            }
            if (options.ContainsKey(key)) throw new ArgumentException("Duplicate option.");
            if (command == "replay" && key is "--riven" or "--offline") options[key] = null;
            else if ((key is "--data-dir" or "--port" || command == "replay" && key is "--scale" or "--output") && i + 1 < args.Length) options[key] = args[++i];
            else throw new ArgumentException("Unsupported command option.");
        }
        var directory = Path.GetFullPath(options.GetValueOrDefault("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarframeCompanion"));
        if (command != "replay")
        {
            var saved = File.Exists(Path.Combine(directory, "state.sqlite")) ? new StateStore(Path.Combine(directory, "state.sqlite")).Read<JsonObject>("settings") : null;
            var port = options.GetValueOrDefault("--port") is { } number ? int.Parse(number, CultureInfo.InvariantCulture) : (int)J.N(saved, "port", 8642);
            if (port is < 1024 or > 65535) throw new ArgumentException("Invalid port.");
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(65) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", WarframeCompanion.Host.LocalApiCredential.Read(directory, port));
            var route = command == "trigger" ? "rewards/scan" : command == "riven" ? "riven/scan" : "quit";
            using var response = await client.PostAsync($"http://127.0.0.1:{port}/api/{route}", new StringContent("{}", Encoding.UTF8, "application/json"));
            Console.WriteLine(await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        if (frames.Count == 0 || frames.Any(p => !File.Exists(p))) throw new ArgumentException("Replay requires existing screenshot files.");
        double? scale = options.GetValueOrDefault("--scale") is { } percent ? double.Parse(percent, CultureInfo.InvariantCulture) / 100 : null;
        if (scale is { } value && (!double.IsFinite(value) || value is < .3 or > 2)) throw new ArgumentException("Interface scale must be 30–200 percent.");
        await using var runtime = new CompanionRuntime(directory, options.ContainsKey("--offline"));
        await runtime.Reload();
        if (runtime.Catalog.LoadedAt == null) throw new IOException("Replay requires a game catalog or populated offline cache.");
        var lines = new List<string>(); var failed = false;
        foreach (var frame in frames)
        {
            var result = options.ContainsKey("--riven") ? await runtime.ScanRiven(CancellationToken.None, frame, scale) : await runtime.ScanRewards(false, CancellationToken.None, frame, scale);
            lines.Add(result.ToJsonString()); failed |= J.S(result, "error") != null;
        }
        var output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        if (options.GetValueOrDefault("--output") is { } path) CachedJson.AtomicWrite(Path.GetFullPath(path), Encoding.UTF8.GetBytes(output));
        else Console.Write(output);
        return failed ? 1 : 0;
    }
}
