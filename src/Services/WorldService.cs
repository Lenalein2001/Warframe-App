using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WarframeCompanion.Services;

public sealed class WorldService(CachedJson cache, string platform)
{
    private JsonNode? raw;
    public string? Error { get; private set; }
    public string? NightwaveTag => Regex.Replace(J.S(J.Get(raw, "nightwave"), "tag") ?? "", @"\s+", "");
    public double? SpiralBegan => Epoch(J.S(raw?["duviriCycle"], "activation"));
    public static JsonNode Duviri(GameView view, JsonNode offers, double? spiral)
    {
        var slots = new[] { (3, "Warframes"), (1, "Primary"), (0, "Secondary"), (5, "Melee"), (-1, "Other") };
        var groups = slots.Select(s => new { slot = s.Item2, picks = J.A(offers, "offers").Where(p => s.Item1 == -1 ? !new long[] { 3, 1, 0, 5 }.Contains(J.N(p, "category")) : J.N(p, "category") == s.Item1).Select(p => {
            var uid = J.S(p, "uid") ?? ""; var item = view.Catalog.Items.GetValueOrDefault(uid); return new { uid, name = view.Catalog.NameOf(uid), owned = J.B(p?["owned"]), mastered = item != null && view.Status(item).Status == "mastered", image = view.Catalog.ImageFor(uid) }; }).ToArray() }).Where(g => g.picks.Length > 0);
        return J.Node(new { groups, rolled = J.D(offers["rolled"]), stale = J.D(offers["rolled"]) < spiral });
    }
    public async Task Refresh(CancellationToken cancellation = default)
    {
        try
        {
            var data = await cache.Get("https://api.warframestat.us/" + platform, TimeSpan.FromSeconds(60), cancellation: cancellation);
            raw = data.Data; Error = data.Warning;
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        { Error = "World state is unavailable; check the network or use the previous cache."; }
    }
    public static DateTimeOffset NextDaily(DateTimeOffset now) => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
    public static DateTimeOffset NextWeekly(DateTimeOffset now)
    {
        var midnight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var days = ((int)DayOfWeek.Monday - (int)midnight.DayOfWeek + 7) % 7;
        return midnight.AddDays(days == 0 ? 7 : days);
    }
    public static double? Epoch(string? text) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time.ToUnixTimeMilliseconds() / 1000.0 : null;
    public JsonNode Fissures(GameView view, JsonNode? watch)
    {
        var picks = view.RelicPicks(); var groups = J.O(picks).Select(p => p.Value).OfType<JsonNode>().ToArray();
        var best = groups.Select(g => J.Get(g, "best")).OfType<JsonNode>().OrderByDescending(b => J.N(b, "needs")).ThenByDescending(b => J.D(J.Get(b, "ev")) ?? 0).FirstOrDefault();
        var all = J.Node(new { owned = groups.Sum(g => J.N(g, "owned")), kinds = groups.Sum(g => J.N(g, "kinds")), best });
        var rows = J.A(raw, "fissures").Where(f => !J.B(J.Get(f, "expired"))).Select(f =>
        {
            var storm = J.B(J.Get(f, "isStorm")); var hard = J.B(J.Get(f, "isHard")); var tier = J.S(f, "tier"); var mission = J.S(f, "missionType");
            return new { id = J.S(f, "id"), tier, mission, node = J.S(f, "node"), enemy = J.S(f, "enemy"), activation = J.S(f, "activation"), expiry = J.S(f, "expiry"), storm, hard, tierNum = J.N(f, "tierNum", 9),
                relics = tier == "Omnia" ? all : J.Get(picks, tier ?? ""), watch = $"{tier}|{mission}|{(storm ? "storm" : hard ? "steel" : "normal")}" };
        }).OrderBy(f => f.storm).ThenBy(f => f.hard).ThenBy(f => f.tierNum).ThenBy(f => f.expiry, StringComparer.Ordinal);
        return J.Node(new { fissures = rows, error = Error, watch = watch ?? new JsonArray() });
    }
    public HashSet<string> OpenEras => J.A(raw, "fissures").Where(f => !J.B(J.Get(f, "expired")) && !J.B(J.Get(f, "isStorm"))).Select(f => J.S(f, "tier")).OfType<string>().ToHashSet();
    public static string RewardText(JsonNode? reward)
    {
        var result = J.A(reward, "items").Select(J.S).OfType<string>().ToList();
        foreach (var c in J.A(reward, "countedItems")) result.Add((J.N(c, "count") > 1 ? J.N(c, "count") + "x " : "") + J.S(c, "type"));
        if (J.N(reward, "credits") > 0) result.Add(J.N(reward, "credits") + " credits");
        return string.Join(", ", result);
    }
    private static JsonNode Describe(JsonNode? value) => J.Node(new { name = J.S(value, "name"), description = J.S(value, "description") is { } d && !d.StartsWith("[PH]", StringComparison.Ordinal) ? d : null });
    public JsonNode Summary(Catalog catalog)
    {
        if (raw == null) return J.Node(new { error = Error ?? "World state is loading." });
        var vt = J.Get(raw, "voidTrader"); var sortie = J.Get(raw, "sortie"); var archon = J.Get(raw, "archonHunt"); var sp = J.Get(raw, "steelPath"); var nw = J.Get(raw, "nightwave");
        var now = DateTimeOffset.UtcNow; var time = now.ToUnixTimeMilliseconds() / 1000.0;
        var cycles = new[] { ("cetusCycle", "Cetus", "Plains of Eidolon"), ("vallisCycle", "Orb Vallis", "Venus"), ("cambionCycle", "Cambion Drift", "Deimos"), ("zarimanCycle", "Zariman", "Faction"), ("duviriCycle", "Duviri", "Spiral"), ("earthCycle", "Earth", "Day and night") }
            .Select(p => new { name = p.Item2, place = p.Item3, state = J.S(J.Get(raw, p.Item1), "state"), expiry = J.S(J.Get(raw, p.Item1), "expiry") }).Where(c => c.expiry != null);
        var deal = J.A(raw, "dailyDeals").FirstOrDefault();
        return J.Node(new {
            fetched = J.S(raw, "timestamp"), cycles,
            baro = new { active = Epoch(J.S(vt, "activation")) is { } start && Epoch(J.S(vt, "expiry")) is { } end && start <= time && time < end,
                location = J.S(vt, "location"), activation = J.S(vt, "activation"), expiry = J.S(vt, "expiry"), inventory = J.A(vt, "inventory").Select(i => new { item = J.S(i, "item"), ducats = J.N(i, "ducats"), credits = J.N(i, "credits"), image = catalog.ArtByName(J.S(i, "item") ?? "") }) },
            sortie = new { boss = J.S(sortie, "boss"), faction = J.S(sortie, "faction"), expiry = J.S(sortie, "expiry"), missions = J.A(sortie, "variants").Select(m => new { type = J.S(m, "missionType"), node = J.S(m, "node"), modifier = J.S(m, "modifier") }) },
            archon = new { boss = J.S(archon, "boss"), expiry = J.S(archon, "expiry"), missions = J.A(archon, "missions").Select(m => new { type = J.S(m, "type"), node = J.S(m, "node") }) },
            steelPath = new { reward = J.S(J.Get(sp, "currentReward"), "name"), cost = J.N(J.Get(sp, "currentReward"), "cost"), expiry = J.S(sp, "expiry"), image = catalog.ArtByName(J.S(J.Get(sp, "currentReward"), "name") ?? "") },
            nightwave = new { season = J.Get(nw, "season"), expiry = J.S(nw, "expiry"), challenges = J.A(nw, "activeChallenges").Select(c => new { title = J.S(c, "title"), desc = J.S(c, "desc"), reputation = J.N(c, "reputation"), daily = J.B(J.Get(c, "isDaily")), elite = J.B(J.Get(c, "isElite")), expiry = J.S(c, "expiry") }) },
            darvo = deal == null ? null : new { item = J.S(deal, "item"), price = J.N(deal, "salePrice"), originalPrice = J.N(deal, "originalPrice"), discount = J.N(deal, "discount"), stock = J.N(deal, "total"), sold = J.N(deal, "sold"), expiry = J.S(deal, "expiry"), image = catalog.ArtByName(J.S(deal, "item") ?? "") },
            resets = new { daily = NextDaily(now).ToString("O"), weekly = NextWeekly(now).ToString("O") },
            alerts = J.A(raw, "alerts").Select(a => { var m = J.Get(a, "mission"); var reward = RewardText(J.Get(m, "reward")); return new { type = J.S(m, "type"), node = J.S(m, "node"), faction = J.S(m, "faction"), what = J.S(m, "description"), reward, expiry = J.S(a, "expiry"), rewards = reward.Split(", ", StringSplitOptions.RemoveEmptyEntries).GroupBy(n => n).Select(g => new { name = g.Key, count = g.Count(), image = catalog.ArtByName(g.Key) }) }; }),
            invasions = J.A(raw, "invasions").Where(i => !J.B(J.Get(i, "completed"))).Select(i => { var ar = RewardText(J.Get(J.Get(i, "attacker"), "reward")); var dr = RewardText(J.Get(J.Get(i, "defender"), "reward")); return new {
                node = J.S(i, "node"), what = J.S(i, "desc"), completion = J.D(J.Get(i, "completion")), attacker = J.S(J.Get(i, "attacker"), "faction"), attackerReward = ar, defender = J.S(J.Get(i, "defender"), "faction"), defenderReward = dr, attackerImage = catalog.ArtByName(ar), defenderImage = catalog.ArtByName(dr) }; }),
            bounties = J.A(raw, "syndicateMissions").Where(s => J.A(s, "jobs").Any()).Select(s => new { syndicate = J.S(s, "syndicate"), jobs = J.A(s, "jobs").Count(), expiry = J.S(s, "expiry") }),
            circuit = J.A(J.Get(raw, "duviriCycle"), "choices").Select(c => new { steel = J.S(c, "category") == "hard", choices = J.A(c, "choices").Select(J.S).OfType<string>().Select(n => Regex.Replace(n, "(?<!^)([A-Z])", " $1")).Select(n => new { name = n, image = catalog.ArtByName(n) }) }),
            archimedea = J.A(raw, "archimedeas").Select(a => new { name = Regex.Replace(J.S(a, "typeKey") ?? "", @"\s+", "") switch { "CT_LAB" => "Deep Archimedea", "CT_HEX" => "Temporal Archimedea", _ => "Archimedea" }, expiry = J.S(a, "expiry"),
                missions = J.A(a, "missions").Select(m => new { type = J.S(m, "missionType"), faction = J.S(m, "faction"), deviation = Describe(J.Get(m, "deviation")), risks = J.A(m, "risks").Select(r => { var described = Describe(r); described["elite"] = J.B(J.Get(r, "isHard")); return described; }) }), modifiers = J.A(a, "personalModifiers").Select(Describe) }),
            events = J.A(raw, "events").Select(e => new { name = J.S(e, "description"), node = J.S(e, "node"), expiry = J.S(e, "expiry"), score = J.D(J.Get(e, "currentScore")), goal = J.D(J.Get(e, "maximumScore")) }),
            varzia = new { location = J.S(J.Get(raw, "vaultTrader"), "location"), expiry = J.S(J.Get(raw, "vaultTrader"), "expiry") }, error = Error
        });
    }
}
