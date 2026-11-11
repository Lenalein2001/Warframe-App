using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WarframeCompanion.Services;

public sealed record LogSignal(string Kind, string? Value = null, string? Id = null, int? Rank = null, int? RankMax = null, double? Time = null, bool Sure = false, bool Historical = false);
public sealed class GameLog
{
    public string? Path { get; private set; }
    public bool Active { get; private set; }
    public string? Player { get; private set; }
    public string? AccountId { get; private set; }
    public double? Scale { get; private set; }
    private long offset;
    private string pending = "";
    private (ulong Device, ulong File) identity;
    private long historicalUntil;
    private DateTimeOffset? logStarted;
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private bool skipPartialLine;
    private bool rewardOpen;
    private (string Path, DateTime Modified, long Length)? configStamp;
    public event Action<LogSignal>? Signal;
    public static string StripGlyphs(string text) => new(text.Where(c => c is < '\ue000' or > '\uf8ff').ToArray());
    public static string? PlayerName(string? text) => text == null ? null : StripGlyphs(text).Trim();
    public static IEnumerable<LogSignal> Candidates(string line, string? player)
    {
        var named = !string.IsNullOrWhiteSpace(player) && Regex.IsMatch(line, @"(?<![\p{L}\p{N}_])" + Regex.Escape(StripGlyphs(player)) + @"(?![\p{L}\p{N}_])");
        var ownReward = line.Contains("VoidProjections: Sending reward info from", StringComparison.Ordinal) && line.Contains("to host!", StringComparison.Ordinal);
        if (!named && !ownReward) yield break;
        var ids = Regex.Matches(line, @"(?<![0-9a-f])[0-9a-f]{24}(?![0-9a-f])").Select(m => m.Value).Where(id => id.Any(c => c != '0')).ToArray();
        foreach (var id in ids)
            yield return new("candidate", ownReward ? "Local client sent relic reward information to the host." : "Game log names the local player alongside this account ID.", id, Sure: ownReward && ids.Length == 1);
    }
    public static IEnumerable<LogSignal> Parse(string line)
    {
        var name = Regex.Match(line, @"(?:Sys \[Info\]: Logged in (\S+)\s*$|Logged in (\S+) \(|Player name changed to (\S+))");
        var id = Regex.Match(line, @"(?:AccountId:\s*|Logged in \S+ \()([0-9a-f]{24})\b");
        if (name.Success) yield return new("login", StripGlyphs(name.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value), id.Success ? id.Groups[1].Value : null);
        else if (id.Success) yield return new("identity", Id: id.Groups[1].Value);
        var badge = Regex.Match(line, @"OnMasteryIconReady1\(Loaded:[^)]*?/Rank(\d+)(?:to(\d+))?_");
        if (badge.Success) yield return new("badge", Rank: int.Parse(badge.Groups[1].Value, CultureInfo.InvariantCulture), RankMax: badge.Groups[2].Success ? int.Parse(badge.Groups[2].Value, CultureInfo.InvariantCulture) : null);
        if (new[] { "OpenVoidProjectionRewardScreen", "ProjectionRewardChoice.lua: Pause countdown done", "VoidProjections: GetVoidProjectionRewards", "ProjectionRewardChoice.lua: Got rewards" }.Any(s => line.Contains(s, StringComparison.Ordinal))) yield return new("rewards");
        if (line.Contains("Relic reward screen shut down", StringComparison.Ordinal)) yield return new("rewards-closed");
        if (new[] { "MissionSucceeded", "MissionFailed", "EndOfMatch.lua: Mission Succeeded", "EndOfMatch.lua: Mission Failed", "ExitLevel", "Inventory updated" }.Any(s => line.Contains(s, StringComparison.OrdinalIgnoreCase))) yield return new("sync");
        if (line.Contains("RollDuviriOffers", StringComparison.Ordinal) || line.Contains("Cave selection for category:", StringComparison.Ordinal))
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
        return ProtonPaths.FindLog(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH"));
    }
    public void Poll(string? configured)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? Discover() : configured;
        Active = false; if (path == null || !File.Exists(path)) return;
        var info = new FileInfo(path); var fileIdentity = NativeEngine.FileIdentity(path);
        var first = path != Path || info.Length < offset || fileIdentity != identity;
        if (first) { Path = path; identity = fileIdentity; offset = Math.Max(0, info.Length - 64 * 1024 * 1024); historicalUntil = info.Length; pending = ""; Player = null; AccountId = null; logStarted = null; decoder.Reset(); skipPartialLine = offset > 0; rewardOpen = false; }
        var historical = offset < historicalUntil;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); stream.Seek(offset, SeekOrigin.Begin);
        // Do not mix historical and newly appended events in one batch.
        var available = (historical ? Math.Min(historicalUntil, stream.Length) : stream.Length) - offset;
        var bytes = new byte[(int)Math.Min(8 * 1024 * 1024, available)]; var count = stream.Read(bytes); offset += count;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
        var decoded = decoder.GetChars(bytes, 0, count, chars, 0, false);
        var lines = (pending + new string(chars, 0, decoded)).Split('\n'); pending = lines[^1].Length <= 65536 ? lines[^1] : "";
        foreach (var line in lines.SkipLast(1))
        {
            if (skipPartialLine) { skipPartialLine = false; continue; }
            var header = Regex.Match(line, @"\[UTC:\s*(.*?)\]");
            if (header.Success && DateTimeOffset.TryParseExact(Regex.Replace(header.Groups[1].Value, @"\s+", " "), ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var began)) logStarted = began;
            var prefix = Regex.Match(line, @"^\s*(\d+(?:\.\d+)?)");
            double? time = logStarted != null && prefix.Success && double.TryParse(prefix.Value, CultureInfo.InvariantCulture, out var elapsed) ? logStarted.Value.ToUnixTimeMilliseconds() / 1000.0 + elapsed : historical ? null : J.Now;
            foreach (var signal in Parse(line.TrimEnd('\r')))
            {
                if (!historical && signal.Kind == "rewards") { if (rewardOpen && !line.Contains("OpenVoidProjectionRewardScreen", StringComparison.Ordinal)) continue; rewardOpen = true; }
                if (!historical && signal.Kind == "rewards-closed") rewardOpen = false;
                if (signal.Kind == "login") { Player = signal.Value; AccountId = signal.Id; }
                if (signal.Kind == "identity") AccountId = signal.Id;
                // Never act on historical mission/reward events while opening an existing log.
                if (!historical || signal.Kind is "login" or "identity" or "badge" || signal.Kind.StartsWith("duviri-", StringComparison.Ordinal)) Signal?.Invoke(signal with { Time = time, Historical = historical });
            }
            foreach (var candidate in Candidates(line, Player)) Signal?.Invoke(candidate with { Time = time });
        }
        Active = true;
        var cfg = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "EE.cfg");
        var configInfo = new FileInfo(cfg);
        if (!configInfo.Exists) { configStamp = null; Scale = null; }
        else if (configInfo.Length < 2 * 1024 * 1024)
        {
            var stamp = (cfg, configInfo.LastWriteTimeUtc, configInfo.Length);
            if (configStamp == stamp) return;
            using var file = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(file);
            var match = Regex.Match(reader.ReadToEnd(), @"Flash\.FlashDrawScale\s*=\s*([0-9.]+)");
            Scale = match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var scale) && scale is >= .3 and <= 2 ? scale : null;
            configStamp = stamp;
        }
    }
}
