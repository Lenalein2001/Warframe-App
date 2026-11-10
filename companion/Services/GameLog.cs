using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WarframeCompanion.Services;

public sealed record LogSignal(string Kind, string? Value = null, string? Id = null, int? Rank = null, int? RankMax = null, double? Time = null);
public sealed class GameLog
{
    public string? Path { get; private set; }
    public bool Active { get; private set; }
    public string? Player { get; private set; }
    public string? AccountId { get; private set; }
    public double? Scale { get; private set; }
    private long offset;
    private string pending = "";
    private DateTime creation;
    private long historicalUntil;
    private DateTimeOffset? logStarted;
    public event Action<LogSignal>? Signal;
    public static string StripGlyphs(string text) => new(text.Where(c => c is < '\ue000' or > '\uf8ff').ToArray());
    public static IEnumerable<LogSignal> Parse(string line)
    {
        var name = Regex.Match(line, @"(?:Sys \[Info\]: Logged in (\S+)\s*$|Logged in (\S+) \(|Player name changed to (\S+))");
        var id = Regex.Match(line, @"(?:AccountId:\s*|Logged in \S+ \()([0-9a-f]{24})\b");
        if (name.Success) yield return new("login", StripGlyphs(name.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value), id.Success ? id.Groups[1].Value : null);
        else if (id.Success) yield return new("identity", Id: id.Groups[1].Value);
        var badge = Regex.Match(line, @"OnMasteryIconReady1\(Loaded:[^)]*?/Rank(\d+)(?:to(\d+))?_");
        if (badge.Success) yield return new("badge", Rank: int.Parse(badge.Groups[1].Value, CultureInfo.InvariantCulture), RankMax: badge.Groups[2].Success ? int.Parse(badge.Groups[2].Value, CultureInfo.InvariantCulture) : null);
        if (line.Contains("OpenVoidProjectionRewardScreen", StringComparison.Ordinal)) yield return new("rewards");
        if (line.Contains("Relic reward screen shut down", StringComparison.Ordinal)) yield return new("rewards-closed");
        if (new[] { "MissionSucceeded", "MissionFailed", "ExitLevel", "Inventory updated" }.Any(s => line.Contains(s, StringComparison.OrdinalIgnoreCase))) yield return new("sync");
        if (line.Contains("RollDuviriOffers", StringComparison.Ordinal))
        {
            if (line.Contains("Generating new Duviri offers", StringComparison.Ordinal)) yield return new("duviri-start");
            var offer = Regex.Match(line, @"(/Lotus/\S+) generated as an (owned|unowned) Cave selection for category:\s*(\d+)");
            if (offer.Success) yield return new("duviri-item", offer.Groups[1].Value, offer.Groups[2].Value, int.Parse(offer.Groups[3].Value, CultureInfo.InvariantCulture));
            if (line.Contains("Duviri Seed:", StringComparison.Ordinal)) yield return new("duviri-done");
        }
    }
    public static string? Discover()
    {
        if (OperatingSystem.IsWindows()) { var p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Warframe", "EE.log"); return File.Exists(p) ? p : null; }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var basePath in new[] { ".steam/steam", ".local/share/Steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
        {
            var p = System.IO.Path.Combine(home, basePath, "steamapps/compatdata/230410/pfx/drive_c/users/steamuser/AppData/Local/Warframe/EE.log"); if (File.Exists(p)) return p;
        }
        return null;
    }
    public void Poll(string? configured)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? Discover() : configured;
        Active = false; if (path == null || !File.Exists(path)) return;
        var info = new FileInfo(path); var first = path != Path || info.Length < offset || info.CreationTimeUtc != creation;
        if (first) { Path = path; creation = info.CreationTimeUtc; offset = Math.Max(0, info.Length - 8 * 1024 * 1024); historicalUntil = info.Length; pending = ""; Player = null; AccountId = null; logStarted = null; }
        var historical = offset < historicalUntil;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[(int)Math.Min(1024 * 1024, stream.Length - offset)]; var count = stream.Read(bytes); offset += count;
        var lines = (pending + Encoding.UTF8.GetString(bytes, 0, count)).Split('\n'); pending = lines[^1].Length <= 65536 ? lines[^1] : "";
        foreach (var line in lines.SkipLast(1))
        {
            var header = Regex.Match(line, @"\[UTC:\s*(.*?)\]");
            if (header.Success && DateTimeOffset.TryParseExact(Regex.Replace(header.Groups[1].Value, @"\s+", " "), ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var began)) logStarted = began;
            var prefix = Regex.Match(line, @"^\s*(\d+(?:\.\d+)?)");
            double? time = logStarted != null && prefix.Success && double.TryParse(prefix.Value, CultureInfo.InvariantCulture, out var elapsed) ? logStarted.Value.ToUnixTimeMilliseconds() / 1000.0 + elapsed : historical ? null : J.Now;
            foreach (var signal in Parse(line.TrimEnd('\r')))
            {
                if (signal.Kind == "login") { Player = signal.Value; AccountId = signal.Id; }
                if (signal.Kind == "identity") AccountId = signal.Id;
                // Never act on historical mission/reward events while opening an existing log.
                if (!historical || signal.Kind is "login" or "identity" or "badge" || signal.Kind.StartsWith("duviri-", StringComparison.Ordinal)) Signal?.Invoke(signal with { Time = time });
            }
        }
        Active = true;
        var cfg = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "EE.cfg");
        if (File.Exists(cfg) && new FileInfo(cfg).Length < 2 * 1024 * 1024)
        {
            using var file = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(file);
            var match = Regex.Match(reader.ReadToEnd(), @"Flash\.FlashDrawScale\s*=\s*([0-9.]+)");
            if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var scale) && scale is >= .3 and <= 2) Scale = scale;
        }
    }
}
