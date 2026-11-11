using System.Text.RegularExpressions;

namespace WarframeCompanion.Services;

public static class ProtonPaths
{
    // Steam libraries may live on other disks. Read Steam's small manifest rather
    // than walking disks or assuming the game uses the default library.
    public static string? FindLog(string home, string? compatibilityData = null)
    {
        var libraries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relative in new[] { ".steam/steam", ".local/share/Steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
        {
            var root = Path.Combine(home, relative); libraries.Add(root);
            var manifest = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024) continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(manifest), "\"(?:path|[0-9]+)\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase))
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
                    if (Path.IsPathFullyQualified(path)) libraries.Add(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var prefixes = libraries.Select(library => Path.Combine(library, "steamapps", "compatdata", "230410"));
        if (!string.IsNullOrWhiteSpace(compatibilityData)) prefixes = prefixes.Prepend(compatibilityData);
        string? latest = null; var modified = DateTime.MinValue;
        foreach (var prefix in prefixes)
        {
            var log = Path.Combine(prefix, "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "Warframe", "EE.log");
            try
            {
                if (File.Exists(log) && File.GetLastWriteTimeUtc(log) > modified) { latest = log; modified = File.GetLastWriteTimeUtc(log); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return latest;
    }
}
