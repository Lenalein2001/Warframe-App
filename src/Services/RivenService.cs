using System.Text.Json;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed record RivenStat(string Name, string Unit, double?[] Bases, string Sign, bool Spliced);
public sealed record RivenData(RivenStat[] Stats, Dictionary<string, string> Wording, Dictionary<string, string> Slugs);
public sealed record GradedStat(string Stat, double Value, double Centre, double Deviation, string Grade, bool Negative, bool OutOfRange, bool SignConflict);
public sealed class RivenService(CachedJson cache)
{
    public static readonly RivenData Data = JsonSerializer.Deserialize<RivenData>(typeof(RivenService).Assembly.GetManifestResourceStream("WarframeCompanion.Services.riven-data.json")!, J.Options)!;
    private JsonObject prices = new();
    private readonly object sync = new();
    private string selectedPlatform = "pc";
    public void SetPlatform(string platform) { lock (sync) { if (platform == selectedPlatform) return; selectedPlatform = platform; prices = new(); Error = null; } }
    public string? Error { get; private set; }
    public static (double Bonus, double Malus) Multipliers(int positives, int negatives) => (positives, negatives) switch
    { (2, 0) => (.99, 0), (2, 1) => (1.2375, .495), (3, 0) => (.75, 0), (3, 1) => (.9375, .75), _ => throw new InventoryException("A riven has 2–3 positives and 0–1 negatives.") };
    public static string Grade(double deviation) => deviation switch { >= 7.5 => "A+", >= 5 => "A", >= 2.5 => "A-", >= 0 => "B+", >= -2.5 => "B", >= -5 => "B-", >= -7.5 => "C", _ => "C-" };
    private static int Class(GameItem item) => item.RivenClass switch { "shotgun" => 1, "pistol" => 2, "archgun" => 3, "melee" => 4, _ => 0 };
    public static GradedStat? Roll(GameItem item, string stat, double value, int pos, int neg, bool negative)
    {
        var s = Data.Stats.FirstOrDefault(s => s.Name.Equals(stat, StringComparison.OrdinalIgnoreCase)); var (bonus, malus) = Multipliers(pos, neg);
        if (s?.Bases[Class(item)] is not { } basis || item.Disposition is not > 0 || !double.IsFinite(value)) return null;
        var centre = basis * item.Disposition.Value * (negative ? malus : bonus); if (centre == 0) return null;
        var dev = (Math.Abs(value) - centre) / centre * (negative ? -100 : 100);
        return new(s.Name, value, Math.Round(centre, 2), Math.Round(dev, 3), Grade(dev), negative, Math.Abs(dev) > 10.01, negative ? s.Sign == "PositiveOnly" : s.Sign == "NegativeOnly");
    }
    public static (int Pos, int Neg, int Unread) Infer(GameItem item, (string Stat, double Value, bool Negative)[] stats)
    {
        var neg = stats.Count(s => s.Negative); var pos = stats.Length - neg; var read = (Pos: Math.Clamp(pos, 2, 3), Neg: Math.Min(neg, 1), Unread: 0);
        bool Fits(int p, int n) => stats.All(s => Roll(item, s.Stat, s.Value, p, n, s.Negative) is { OutOfRange: false });
        if (Fits(read.Pos, read.Neg)) return read;
        var possible = new[] { (2, 0), (2, 1), (3, 0), (3, 1) }.Where(c => c.Item1 >= pos && c.Item2 >= neg && Fits(c.Item1, c.Item2)).ToArray();
        return possible.Length == 1 ? (possible[0].Item1, possible[0].Item2, Math.Max(0, possible[0].Item1 + possible[0].Item2 - stats.Length)) : read;
    }
    public static GameItem Weapon(Catalog cat, string name) => cat.Items.Values.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && i.Disposition > 0) ?? throw new InventoryException("No riven data for that weapon.");
    public static JsonNode Table(Catalog cat, string name, int pos, int neg)
    {
        var item = Weapon(cat, name); var (bonus, malus) = Multipliers(pos, neg);
        var rows = Data.Stats.Where(s => s.Bases[Class(item)] != null).Select(s => {
            var basis = s.Bases[Class(item)]!.Value * item.Disposition!.Value; var centre = basis * bonus; var minus = basis * malus;
            return new { stat = s.Name, unit = s.Unit, min = Math.Round(centre * .9, 2), centre = Math.Round(centre, 2), max = Math.Round(centre * 1.1, 2), spliced = s.Spliced,
                negMin = malus == 0 ? (double?)null : Math.Round(minus * .9, 2), negCentre = malus == 0 ? (double?)null : Math.Round(minus, 2), negMax = malus == 0 ? (double?)null : Math.Round(minus * 1.1, 2) }; });
        return J.Node(new { weapon = item.Name, @class = item.RivenClass, disposition = Math.Round(item.Disposition!.Value, 3), dots = item.DispoDots, positives = pos, negatives = neg, stats = rows });
    }
    public static JsonNode GradeBody(Catalog cat, JsonNode body)
    {
        var item = Weapon(cat, J.S(body, "weapon") ?? ""); var pos = checked((int)J.N(body, "positives", 3)); var neg = checked((int)J.N(body, "negatives", 1)); Multipliers(pos, neg);
        var stats = J.A(body, "stats").Select(s => Roll(item, J.S(s, "stat") ?? "", J.D(J.Get(s, "value")) ?? double.NaN, pos, neg, J.B(J.Get(s, "negative")))).OfType<GradedStat>();
        return J.Node(new { weapon = item.Name, disposition = Math.Round(item.Disposition!.Value, 3), dots = item.DispoDots, stats });
    }
    public async Task Refresh(string platform, CancellationToken cancellation)
    {
        try
        {
            var result = await cache.Get($"https://api.warframestat.us/{platform}/rivens", TimeSpan.FromHours(6), cancellation: cancellation);
            var next = new JsonObject(); foreach (var (_, group) in J.O(result.Data)) foreach (var (weapon, kinds) in J.O(group)) if (kinds is JsonObject) next[weapon] = kinds.DeepClone();
            if (next.Count == 0) throw new IOException("No riven prices returned."); lock (sync) { if (platform == selectedPlatform) { prices = next; Error = result.Warning; } }
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or JsonException or TaskCanceledException) { lock (sync) { if (platform == selectedPlatform) Error = "Traded riven prices are unavailable."; } }
    }
    public JsonNode? Traded(string name) => prices[name]?.DeepClone();
    public JsonNode Rows(Catalog cat) => J.Node(new { error = Error, rows = cat.Items.Values.Where(i => i.Disposition > 0).OrderByDescending(i => i.Disposition).Select(i => new {
        name = i.Name, category = i.Category, image = Catalog.ImageUrl(i.Image), disposition = Math.Round(i.Disposition!.Value, 3), dots = i.DispoDots,
        dotStr = i.DispoDots is >= 1 and <= 5 ? new string('●', (int)i.DispoDots) + new string('○', 5 - (int)i.DispoDots) : "", unrolled = prices[i.Name]?["unrolled"], rerolled = prices[i.Name]?["rerolled"] }) });
}
