using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WarframeCompanion.Services;

public sealed class ArbitrationService(CachedJson cache)
{
    public (long Start, string Tag)[] Slots { get; private set; } = [];
    public Dictionary<string, string> Tiers { get; private set; } = [];
    public static (long Start, string Tag)[] ParseSchedule(string text) => text.Split('\n').Select(l => l.Trim().Split(',')).Where(p => p.Length == 2 && long.TryParse(p[0], out _) && Regex.IsMatch(p[1], "^[a-zA-Z0-9]+$")).Select(p => (long.Parse(p[0], CultureInfo.InvariantCulture), p[1])).OrderBy(p => p.Item1).ToArray();
    public static Dictionary<string, string> ParseTiers(string text) => Regex.Matches(text.Split("};")[0], "(?m)^\\s*([a-zA-Z0-9]+):\\s*\"([SABCDF])\"").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    public async Task Refresh(bool force, CancellationToken cancellation)
    {
        try
        {
            var values = await Task.WhenAll(cache.Blob("https://browse.wf/arbys.txt", TimeSpan.FromDays(1), force, cancellation), cache.Blob("https://browse.wf/supplemental-data/arbyTiers.js", TimeSpan.FromDays(1), force, cancellation));
            var slots = ParseSchedule(Encoding.UTF8.GetString(values[0])); if (slots.Length > 0) Slots = slots; var tiers = ParseTiers(Encoding.UTF8.GetString(values[1])); if (tiers.Count > 0) Tiers = tiers;
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or TaskCanceledException) { }
    }
    public JsonNode Summary(Catalog cat, double time)
    {
        var now = Array.FindLastIndex(Slots, s => s.Start <= time && time < s.Start + 3600);
        JsonNode Row((long Start, string Tag) slot)
        {
            var node = cat.Regions?[slot.Tag]; string Text(string key) => J.S(cat.Dictionary?[J.S(node, key) ?? ""]) is { } text ? CatalogLoader.Clean(text) : "";
            var faction = J.S(node, "faction") switch { "FC_INFESTATION" => "Infested", "FC_OROKIN" => "Corrupted", "FC_MITW" => "The Murmur", var value => CatalogLoader.Clean((value ?? "").Replace("FC_", "").Replace('_', ' ')) };
            return J.Node(new { start = slot.Start, end = slot.Start + 3600, tag = slot.Tag, node = Text("name") is { Length: > 0 } name ? name : slot.Tag, planet = Text("systemName"), mission = Text("missionName"), faction,
                bonus = J.D(node?["darkSectorData"]?["resourceBonus"]) is > 0 and var bonus ? Math.Round(bonus * 100) : (double?)null, tier = Tiers.Count == 0 ? null : Tiers.GetValueOrDefault(slot.Tag, "F") });
        }
        var later = now < 0 ? [] : Slots.Skip(now + 1).ToArray();
        return J.Node(new { current = now < 0 ? null : Row(Slots[now]), upcoming = later.Take(24).Select(Row), next = new[] { "S", "A", "B" }.Select(t => later.FirstOrDefault(s => Tiers.GetValueOrDefault(s.Tag) == t)).Where(s => s.Tag != null).Select(Row), tiers = Tiers.Count > 0 });
    }
}
