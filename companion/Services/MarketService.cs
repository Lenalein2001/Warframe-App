using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace WarframeCompanion.Services;

public sealed record MarketItem(string Slug, string? Name, string? GameRef, long? Ducats, string? Image);
public sealed record MarketPrice(long? Sell, double? SellAvg, long? Buy, double T);

public sealed class MarketService : IAsyncDisposable
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> queued = new();
    private readonly ConcurrentDictionary<string, MarketPrice> prices = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim rate = new(1);
    private DateTimeOffset nextRequest;
    private readonly Task worker;
    private readonly string directory;
    private readonly bool offline;
    private Dictionary<string, MarketItem> items = [];
    private Dictionary<string, string> byRef = [];
    private Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; private set; }
    public int Pending => queued.Count;
    public MarketService(string directory, string platform, bool offline)
    {
        if (!new[] { "pc", "ps4", "xb1", "swi" }.Contains(platform)) throw new ArgumentException("Unknown market platform.");
        this.directory = Path.Combine(directory, "market-" + platform); this.offline = offline;
        Directory.CreateDirectory(this.directory);
        client.DefaultRequestHeaders.Add("Language", "en"); client.DefaultRequestHeaders.Add("Platform", platform); client.DefaultRequestHeaders.Add("Crossplay", "true");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WarframeCompanion/0.1");
        var file = Path.Combine(this.directory, "prices.json");
        if (File.Exists(file))
        {
            try { foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, MarketPrice>>(File.ReadAllText(file), J.Options) ?? []) prices[pair.Key] = pair.Value; }
            catch (JsonException) { Error = "Market price cache is corrupt; prices will be refreshed."; }
        }
        worker = Run();
    }
    public async Task Load(CancellationToken cancellation = default)
    {
        using var cache = new CachedJson(directory, offline);
        var document = await cache.Get("https://api.warframe.market/v2/items", TimeSpan.FromDays(1), cancellation: cancellation);
        var data = J.Get(document.Data, "data") as JsonArray ?? throw new IOException("Market item catalog has an unexpected schema.");
        var next = new Dictionary<string, MarketItem>(); var refs = new Dictionary<string, string>(); var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in data)
        {
            if (J.S(entry, "slug") is not { } slug) continue;
            var en = J.Get(J.Get(entry, "i18n"), "en"); var name = J.S(en, "name"); var gameRef = J.S(entry, "gameRef");
            var image = J.S(en, "subIcon") ?? J.S(en, "thumb") ?? J.S(en, "icon");
            next[slug] = new(slug, name, gameRef, J.Get(entry, "ducats") == null ? null : J.N(entry, "ducats"), image == null ? null : "https://warframe.market/static/assets/" + image);
            if (name != null) names.TryAdd(name, slug); if (gameRef != null) refs.TryAdd(gameRef, slug);
        }
        if (next.Count == 0) throw new IOException("Market returned no usable item metadata.");
        items = next; byRef = refs; byName = names; Error = document.Warning;
    }
    public string? Slug(string? uid, string? candidate, string? name)
    {
        if (uid != null && byRef.TryGetValue(uid, out var found)) return found;
        if (candidate != null && items.ContainsKey(candidate)) return candidate;
        if (name != null && byName.TryGetValue(name, out found)) return found;
        return candidate;
    }
    public MarketItem? Item(string? slug) => slug != null ? items.GetValueOrDefault(slug) : null;
    public static string? SlotGlyph(string category, string uid)
    {
        var (folder, slots) = category switch
        {
            "Warframes" => ("warframe", new[] { "chassis", "helmet", "systems" }),
            "Archwing" => ("archwing", ["chassis", "systems", "wings"]),
            "Sentinels" => ("sentinel", ["carapace", "cerebrum", "systems"]),
            "Primary" or "Secondary" or "Melee" or "Arch-Gun" or "Arch-Melee" or "SentinelWeapons" =>
                ("weapon", ["aegis", "barrel", "blade", "chassis", "disc", "engine", "gauntlet", "grip", "guard", "handle", "head", "heatsink", "hilt", "limb", "link", "motor", "receiver", "rivet", "stock", "string"]),
            _ => ("", Array.Empty<string>())
        };
        var last = uid.Split('/')[^1].ToLowerInvariant();
        var blueprint = last.EndsWith("blueprint", StringComparison.Ordinal);
        var stem = blueprint ? last[..^9] : last;
        if (stem.EndsWith("component", StringComparison.Ordinal)) stem = stem[..^9];
        var slot = slots.FirstOrDefault(s => stem.EndsWith(s, StringComparison.Ordinal));
        const string assets = "https://warframe.market/static/assets/sub_icons/";
        return slot != null ? assets + folder + "/generic_" + slot + "_128x128.png" : blueprint ? assets + "blueprint_128x128.png" : null;
    }
    public MarketPrice? Price(string? slug, bool request = true, int maxMinutes = 360)
    {
        if (slug == null) return null;
        prices.TryGetValue(slug, out var price);
        if (request && !offline && (price == null || J.Now - price.T > maxMinutes * 60) && items.ContainsKey(slug) && queued.TryAdd(slug, 0))
            if (!queue.Writer.TryWrite(slug)) queued.TryRemove(slug, out _);
        return price;
    }
    public JsonArray Search(string query)
    {
        if (query.Trim().Length < 2) return [];
        return J.Array(items.Values.Where(i => i.Name?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true).Take(30).Select(i => {
            var p = Price(i.Slug); return new { slug = i.Slug, name = i.Name, ducats = i.Ducats, sell = p?.Sell, buy = p?.Buy, image = i.Image }; }));
    }
    public async Task<JsonNode> Get(string url, CancellationToken cancellation)
    {
        await rate.WaitAsync(cancellation);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow; if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(400);
            using var response = await client.GetAsync(url, cancellation);
            if ((int)response.StatusCode == 429)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                nextRequest = DateTimeOffset.UtcNow + (retry > TimeSpan.FromSeconds(1) ? retry : TimeSpan.FromSeconds(1));
            }
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation)) ?? throw new IOException("Market response is empty.");
        }
        finally { rate.Release(); }
    }
    private JsonNode? rivenWeapons;
    public async Task<JsonNode> RivenListings(GameItem item, IEnumerable<string> positiveStats, CancellationToken cancellation)
    {
        if (offline) throw new IOException("Offline mode is enabled.");
        rivenWeapons ??= await Get("https://api.warframe.market/v2/riven/weapons", cancellation);
        var weapons = J.A(rivenWeapons, "data").ToArray();
        var weapon = weapons.FirstOrDefault(w => J.S(w, "gameRef") == item.Uid) ?? weapons.FirstOrDefault(w => string.Equals(J.S(w?["i18n"]?["en"], "name"), item.Name, StringComparison.OrdinalIgnoreCase));
        var slug = J.S(weapon, "slug") ?? throw new IOException("No market riven weapon mapping.");
        var stats = positiveStats.Select(s => RivenService.Data.Slugs.GetValueOrDefault(s)).OfType<string>();
        var query = "type=riven&weapon_url_name=" + Uri.EscapeDataString(slug) + "&positive_stats=" + Uri.EscapeDataString(string.Join(',', stats)) + "&sort_by=price_asc";
        var found = await Get("https://api.warframe.market/v1/auctions/search?" + query + "&buyout_policy=direct", cancellation);
        var auctions = J.A(found["payload"], "auctions").Where(a => J.Get(a, "visible")?.GetValue<bool>() != false && !J.B(J.Get(a, "closed")) && J.N(a, "buyout_price") > 0).ToArray();
        var prices = auctions.Select(a => J.N(a, "buyout_price")).Order().ToArray();
        return J.Node(new { count = prices.Length, lowest = prices.Length > 0 ? (long?)prices[0] : null, median = prices.Length > 0 ? (long?)prices[prices.Length / 2] : null,
            lowestOnline = auctions.Where(a => J.S(a?["owner"], "status") == "ingame").Select(a => (long?)J.N(a, "buyout_price")).DefaultIfEmpty().Min(), url = "https://warframe.market/auctions/search?" + query + "&polarity=any" });
    }
    public static MarketPrice ParseOrders(JsonNode value, double time)
    {
        var data = J.Get(value, "data");
        long[] Side(string side)
        {
            if (J.Get(data, side) is not JsonArray array) throw new IOException("Market orders have an unexpected schema.");
            return array.Select(o => { var price = J.Get(o, "platinum"); if (price is not JsonValue v || !v.TryGetValue<long>(out var n) || n < 0) throw new IOException("Market order has an invalid price."); return n; }).ToArray();
        }
        var sell = Side("sell").Order().ToArray(); var buy = Side("buy");
        return new(sell.Length == 0 ? null : sell[0], sell.Length == 0 ? null : Math.Round(sell.Take(3).Average(), 1, MidpointRounding.ToEven), buy.Length == 0 ? null : buy.Max(), time);
    }
    private async Task Run()
    {
        try
        {
            await foreach (var slug in queue.Reader.ReadAllAsync(stop.Token))
            {
                try
                {
                    var data = await Get("https://api.warframe.market/v2/orders/item/" + Uri.EscapeDataString(slug) + "/top", stop.Token);
                    prices[slug] = ParseOrders(data, J.Now);
                    CachedJson.AtomicWrite(Path.Combine(directory, "prices.json"), JsonSerializer.SerializeToUtf8Bytes(prices, J.Options)); Error = null;
                }
                catch (Exception e) when (!stop.IsCancellationRequested && e is HttpRequestException or IOException or JsonException or TaskCanceledException)
                { Error = "Market prices could not be refreshed; cached prices retain their original timestamps."; }
                finally { queued.TryRemove(slug, out _); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); queue.Writer.TryComplete(); await worker; client.Dispose(); stop.Dispose(); rate.Dispose(); }
}
