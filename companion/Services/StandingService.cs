using System.Text.Json.Nodes;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed class StandingService(GameView view)
{
    private const string Universal = "/Lotus/Types/Items/SyndicateDogTags/UniversalSyndicateDogTag";
    private static readonly string[] Order = ["SteelMeridianSyndicate", "ArbitersSyndicate", "CephalonSudaSyndicate", "PerrinSyndicate", "RedVeilSyndicate", "NewLokaSyndicate", "CetusSyndicate", "QuillsSyndicate", "SolarisSyndicate", "VoxSyndicate", "VentKidsSyndicate", "EntratiSyndicate", "NecraloidSyndicate", "ZarimanSyndicate", "EntratiLabSyndicate", "HexSyndicate", "KahlSyndicate", "NightcapJournalSyndicate", "ConclaveSyndicate", "LibrarySyndicate", "EventSyndicate"];
    private string Text(JsonNode? key, string fallback) => J.S(J.Get(view.Catalog.Dictionary, J.S(key) ?? "")) is { } name ? CatalogLoader.Clean(name) : fallback;
    private string Name(string tag) => Text(J.Get(J.Get(view.Catalog.SyndicateExport, tag), "name"), Catalog.CamelName(tag).Replace(" Syndicate", "", StringComparison.Ordinal));
    private static string? Icon(JsonNode? value) => J.S(value) is { } path && path.StartsWith("/Lotus/", StringComparison.Ordinal) ? "https://browse.wf" + path : null;
    private static string Kind(string tag, JsonNode? s) => tag.StartsWith("RadioLegion", StringComparison.Ordinal) ? "nightwave" : J.S(s, "dailyLimitBin") == "STANDING_LIMIT_BIN_NORMAL" ? "faction" : "other";
    public static string? Pool(string? bin)
    {
        const string prefix = "STANDING_LIMIT_BIN_";
        if (bin?.StartsWith(prefix, StringComparison.Ordinal) != true) return null;
        var suffix = bin[prefix.Length..]; return suffix switch { "NONE" => null, "NORMAL" => "DailyAffiliation", "" => null, _ => "DailyAffiliation" + char.ToUpperInvariant(suffix[0]) + suffix[1..].ToLowerInvariant() };
    }
    public static (long Value, long Size, bool AtCap, bool Full) Progress(JsonNode s, Affiliation affiliation)
    {
        var ranks = J.A(s, "titles").OrderBy(r => J.N(r, "level")).ToArray(); var top = ranks.Select(r => J.N(r, "level")).DefaultIfEmpty().Max();
        if (ranks.Length > 0 && J.N(ranks[^1], "maxStanding") <= 10) return (Math.Max(0, affiliation.Title), top, false, false);
        var rank = ranks.FirstOrDefault(r => J.N(r, "level") == affiliation.Title);
        if (rank != null)
        {
            var min = J.N(rank, "minStanding"); var max = J.N(rank, "maxStanding");
            return affiliation.Title < 0 ? (affiliation.Standing - max, max - min, false, false) :
                (affiliation.Standing - min, max - min, affiliation.Standing >= max && affiliation.Title < top, affiliation.Standing >= max && affiliation.Title >= top);
        }
        var first = ranks.FirstOrDefault(r => J.N(r, "level") == 1); var floor = J.N(first, "minStanding");
        return (affiliation.Standing, floor, floor > 0 && affiliation.Standing >= floor, false);
    }
    public JsonObject Summary(long? masteryRank, string? nightwave)
    {
        var readings = new List<(Standing State, double Time, string Label)>();
        if (view.Profile?.Standing is { } profile) readings.Add((profile, view.Profile.Fetched, "profile"));
        if (view.Inventory?.Standing is { } inventory) readings.Add((inventory, view.Inventory.SourceTimestamp ?? view.Inventory.ReadAt.ToUnixTimeMilliseconds() / 1000.0, "inventory"));
        var syndicates = new Dictionary<string, Affiliation>(); Dictionary<string, long>? daily = null; double dailyTime = 0; long? focus = null; double focusTime = 0;
        foreach (var reading in readings.OrderBy(r => r.Time))
        {
            foreach (var p in reading.State.Syndicates) syndicates[p.Key] = p.Value;
            if (reading.State.Daily.Count > 0) { daily = reading.State.Daily; dailyTime = reading.Time; }
            if (reading.State.DailyFocus.HasValue) { focus = reading.State.DailyFocus; focusTime = reading.Time; }
        }
        var entries = J.O(view.Catalog.SyndicateExport).OrderBy(p => { var i = Array.IndexOf(Order, p.Key); return i < 0 ? Order.Length : i; }).ToArray();
        var rows = entries.Where(p => Kind(p.Key, p.Value) != "nightwave" || p.Key == nightwave).Where(p => p.Key != "EventSyndicate" || syndicates.ContainsKey(p.Key))
            .Select(p => Row(p.Key, p.Value!, syndicates.GetValueOrDefault(p.Key))).ToArray();
        var cap = masteryRank.HasValue ? 16000 + 500 * Math.Max(0, masteryRank.Value) : (long?)null;
        var focusCap = masteryRank.HasValue ? 250000 + 5000 * Math.Max(0, masteryRank.Value) : (long?)null;
        var midnight = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeSeconds();
        var pools = entries.Where(p => Kind(p.Key, p.Value) != "nightwave" && Pool(J.S(p.Value, "dailyLimitBin")) != null).GroupBy(p => Pool(J.S(p.Value, "dailyLimitBin"))!).Select(g =>
        {
            var read = daily?.ContainsKey(g.Key) == true && dailyTime >= midnight; var left = read ? daily![g.Key] : cap;
            return new { key = g.Key, name = g.Key == "DailyAffiliation" ? "Faction syndicates" : string.Join(", ", g.Select(p => Name(p.Key))), left, cap = cap.HasValue ? Math.Max(cap.Value, left ?? 0) : (long?)null, read };
        });
        var focusRead = focus.HasValue && focusTime >= midnight; var focusLeft = focusRead ? focus : focusCap; var latest = readings.OrderByDescending(r => r.Time).FirstOrDefault();
        return (JsonObject)J.Node(new { syndicates = rows, pools, focus = new { left = focusLeft, cap = focusCap.HasValue ? Math.Max(focusCap.Value, focusLeft ?? 0) : (long?)null, read = focusRead }, masteryRank, cap,
            reset = WorldService.NextDaily(DateTimeOffset.UtcNow).ToString("O"), asOf = readings.Count > 0 ? latest.Time : (double?)null, source = readings.Count > 0 ? latest.Label : null, universal = view.HeldStack(Universal), loaded = entries.Length > 0 });
    }
    private JsonObject Row(string tag, JsonNode s, Affiliation? mine)
    {
        var a = mine ?? new Affiliation(0, 0); var progress = Progress(s, a); var ranks = J.A(s, "titles").OrderBy(r => J.N(r, "level")).ToArray();
        var rank = ranks.FirstOrDefault(r => J.N(r, "level") == a.Title); var next = a.Title >= 0 ? ranks.FirstOrDefault(r => J.N(r, "level") == a.Title + 1) : null;
        var medals = J.A(s, "medallions").Where(m => J.S(m, "itemType") is { } uid && uid != Universal && view.HeldStack(uid) > 0).Select(m => {
            var uid = J.S(m, "itemType")!; var name = view.Catalog.NameOf(uid); return new { name, count = view.HeldStack(uid), each = J.N(m, "standing"), image = view.Catalog.ArtByName(name) }; }).ToArray();
        var color = J.S(J.Get(s, "colour"), "value"); color = color?.Length >= 6 && color[^6..].All(Uri.IsHexDigit) ? "#" + color[^6..].ToLowerInvariant() : null;
        return (JsonObject)J.Node(new { tag, name = Name(tag), icon = Icon(J.Get(s, "icon")), colour = color, kind = Kind(tag, s), known = mine != null,
            steps = ranks.Length > 0 && J.N(ranks[^1], "maxStanding") <= 10, standing = a.Standing, title = a.Title,
            rankName = rank == null ? a.Title == 0 ? "Neutral" : "Rank " + a.Title : Text(J.Get(rank, "name"), "Rank " + a.Title), rankIcon = Icon(J.Get(rank, "icon")), topRank = ranks.Length > 0 ? J.N(ranks[^1], "level") : (long?)null,
            progress = progress.Value, size = progress.Size, atCap = progress.AtCap, full = progress.Full,
            next = next == null ? null : new { level = J.N(next, "level"), name = Text(J.Get(next, "name"), "Rank " + J.N(next, "level")), credits = J.N(J.Get(next, "sacrifice"), "credits"),
                items = J.A(J.Get(next, "sacrifice"), "items").Select(i => { var uid = J.S(i, "ItemType") ?? ""; var name = view.Catalog.NameOf(uid); return new { name, count = J.N(i, "ItemCount", 1), have = view.HeldStack(uid), image = view.Catalog.ArtByName(name) }; }) },
            pool = Pool(J.S(s, "dailyLimitBin")), medallions = new { standing = medals.Sum(m => m.count * m.each), items = medals, capped = J.B(J.Get(s, "medallionsCappedByDailyLimit")) },
            allies = J.O(J.Get(s, "alignments")).Select(p => new { tag = p.Key, share = J.D(p.Value), name = Name(p.Key) }), offers = J.A(s, "favours").Count() });
    }
    public JsonNode? Detail(string tag, long? masteryRank)
    {
        var s = J.Get(view.Catalog.SyndicateExport, tag); if (s == null) return null;
        var summary = Summary(masteryRank, tag); var row = J.A(summary, "syndicates").FirstOrDefault(r => J.S(r, "tag") == tag)?.DeepClone(); if (row == null) return null;
        var title = J.N(row, "title"); var waiting = 0;
        var offers = J.A(s, "favours").Select(o => {
            var uid = (J.S(o, "storeItem") ?? "").Replace("/StoreItems", "", StringComparison.Ordinal); var slug = view.Market.Slug(uid, null, null); var price = view.Market.Price(slug);
            if (slug != null && price == null) waiting++; var standing = J.N(o, "standingCost"); var level = J.N(o, "requiredLevel");
            return new { uid, name = view.Market.Item(slug)?.Name ?? view.Catalog.NameOf(uid), image = view.Market.Item(slug)?.Image ?? view.ImageFor(uid), standing, level, available = level <= title, slug, price = price?.Sell,
                perK = price?.Sell is { } sell && standing > 0 ? Math.Round(sell * 1000.0 / standing, 2) : (double?)null };
        }).OrderByDescending(o => o.available).ThenByDescending(o => o.perK ?? -1).ToArray();
        row["offers"] = J.Array(offers); row["pricing"] = waiting;
        row["ranks"] = J.Array(J.A(s, "titles").OrderBy(r => J.N(r, "level")).Select(r => new { level = J.N(r, "level"), name = Text(J.Get(r, "name"), "Rank " + J.N(r, "level")), icon = Icon(J.Get(r, "icon")), min = J.N(r, "minStanding"), max = J.N(r, "maxStanding") }));
        row["pools"] = summary["pools"]?.DeepClone(); return row;
    }
}
