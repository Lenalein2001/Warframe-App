using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;

namespace WarframeCompanion.Services;

public sealed class RegistryState
{
    public string? Active { get; set; }
    public List<AccountState> Accounts { get; set; } = [];
}

public sealed partial class CompanionRuntime : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly StateStore store;
    private readonly SnapshotStore snapshots;
    private readonly CachedJson cache;
    private readonly CatalogLoader loader;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim reload = new(1);
    private readonly HttpClient profileClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim profileGate = new(1);
    private DateTimeOffset nextProfile;
    private RegistryState registry;
    private JsonObject settings;
    private readonly List<JsonNode> events = [];
    private Task? background;
    public Catalog Catalog { get; private set; } = new();
    public MarketService Market { get; private set; }
    public WorldService World { get; private set; }
    public ArbitrationService Arbitrations { get; }
    public RivenService Rivens { get; }
    public ArtService Art { get; }
    public RewardHistory History { get; }
    public UpdateService Updates { get; }
    public string? DataError { get; private set; }
    public double Started { get; } = J.Now;
    public bool Desktop { get; set; }
    public bool Offline { get; }
    public string DataDirectory { get; }
    public event Action? ShowRequested;
    public event Action? QuitRequested;
    public CompanionRuntime(string directory, bool offline, bool migrateLegacy = false)
    {
        DataDirectory = Path.GetFullPath(directory); Offline = offline;
        Directory.CreateDirectory(DataDirectory);
        store = new(Path.Combine(DataDirectory, "state.sqlite")); snapshots = new(Path.Combine(DataDirectory, "companion.sqlite"));
        registry = store.Read<RegistryState>("registry") ?? new(); settings = store.Read<JsonObject>("settings") ?? Settings.Defaults();
        if (migrateLegacy && store.Read<RegistryState>("registry") == null)
        {
            var paths = LegacyMigration.DefaultPaths(); var migrated = LegacyMigration.Load(paths.Data, paths.Config, snapshots);
            if (migrated is { } data) { registry = data.Registry; settings = data.Settings; store.Write("registry", registry); store.Write("settings", settings); Event("Copied existing Rust accounts and settings. Original files were preserved.", "info"); }
        }
        History = new(store);
        if (migrateLegacy) History.ImportLegacy(Path.Combine(LegacyMigration.DefaultPaths().Data, "rewards.jsonl"));
        cache = new(Path.Combine(DataDirectory, "cache"), offline); loader = new(cache);
        Updates = new(cache);
        Art = new(Path.Combine(DataDirectory, "cache", "art"), offline);
        Arbitrations = new(cache);
        Market = new(Path.Combine(DataDirectory, "cache"), J.S(settings, "platform") ?? "pc", offline); World = new(cache, J.S(settings, "platform") ?? "pc"); Rivens = new(cache);
        Rivens.SetPlatform(J.S(settings, "platform") ?? "pc");
    }
    public JsonObject GetSettings() { lock (sync) return (JsonObject)settings.DeepClone(); }
    public void Start(bool watchGame = true) { RewardScreenOpened += StartAutomaticOcr; RewardScreenClosed += CloseRewards; background ??= Background(); if (watchGame) gameLoop ??= GameLoop(); }
    private async Task Background()
    {
        var catalog = Task.WhenAll(Reload(stop.Token), Updates.Check(stop.Token));
        try { await Market.Load(stop.Token); } catch (Exception e) when (!stop.IsCancellationRequested && e is IOException or HttpRequestException or JsonException or TaskCanceledException) { Event("Market metadata is unavailable; prices remain unknown.", "error"); }
        await catalog;
        while (!stop.IsCancellationRequested)
        {
            try { await Task.WhenAll(World.Refresh(stop.Token), Rivens.Refresh(J.S(GetSettings(), "platform") ?? "pc", stop.Token)); NotificationTick(); await Task.Delay(TimeSpan.FromSeconds(60), stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        }
    }
    public async Task Reload(CancellationToken cancellation = default, bool force = false)
    {
        await reload.WaitAsync(cancellation);
        try
        {
            var arbitration = Arbitrations.Refresh(force, cancellation);
            var loaded = await loader.Load(force, cancellation); await arbitration; lock (sync) { Catalog = loaded; DataError = loader.Warnings.Count > 0 ? string.Join(" ", loader.Warnings) : null; }
            Event("Game catalog loaded.", "info");
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or JsonException or TaskCanceledException)
        { DataError = "Game data could not be loaded. Check the network or populate the offline cache, then reload data."; Event(DataError, "error"); }
        finally { reload.Release(); }
    }
    public void Event(string message, string kind)
    {
        lock (sync) { events.Add(J.Node(new { t = J.Now, text = message, kind })); if (events.Count > 100) events.RemoveRange(0, events.Count - 100); }
    }
    private AccountState? Active => registry.Accounts.FirstOrDefault(a => a.Key == registry.Active);
    public GameView View()
    {
        lock (sync)
        {
            var account = Active;
            var copy = account == null ? null : JsonSerializer.Deserialize<AccountState>(JsonSerializer.Serialize(account, J.Options), J.Options);
            return new(Catalog, copy == null ? null : snapshots.Current(copy.Id ?? copy.Key), copy?.Profile, copy?.Manual ?? new(), Market);
        }
    }
    public JsonNode Summary()
    {
        var view = View();
        lock (sync)
        {
            var account = Active; var inv = view.Inventory; var profile = account?.Profile;
            var mastered = Catalog.Items.Values.Count(i => view.Status(i).Status == "mastered");
            var sources = new {
                eeLog = new { path = Log.Path, active = Log.Active, searching = Log.Path == null },
                profile = new { fetched = profile?.Fetched, source = profile?.Source, error = account?.ProfileError, items = profile?.Xp.Count ?? 0 },
                inventory = new { source = inv?.Provider, imported = inv?.ReadAt.ToUnixTimeMilliseconds() / 1000.0, synced = inv?.SourceTimestamp, stacks = inv?.Counts.Count ?? 0 },
                manual = new { counts = view.Manual.Counts.Count, mastered = view.Manual.Mastered.Count },
                memscan = new { enabled = J.B(settings["memory_scan_enabled"]), running = ScanRunning, error = ScanError, state = !J.B(settings["memory_scan_enabled"]) ? "off" : ScanRunning ? "scanning" : ScanError != null ? "error" : "idle", detail = ScanError, progress = 0, next = nextMemory > J.Now ? (double?)nextMemory : null },
                tools = new Dictionary<string, object?> { ["tesseract"] = ScreenCapture.Which("tesseract") != null, ["screenshot"] = ScreenCapture.ScreenshotTool(J.S(settings, "screenshot_tool") ?? "auto"), ["notify-send"] = OperatingSystem.IsWindows() || ScreenCapture.Which("notify-send") != null }, market = new { error = Market.Error, queued = Market.Pending }
            };
            return J.Node(new { ready = Catalog.LoadedAt != null, dataError = DataError, version = UpdateService.Version, os = OperatingSystem.IsWindows() ? "windows" : "linux", desktop = Desktop, update = Updates.Available,
                account = new { key = account?.Key, id = account?.Id, name = account?.Name, source = account?.Source, playing = GameProcess != null && account?.Key == playingKey, logName = Log.Player, evidence = account?.Evidence ?? [], profileError = account?.ProfileError },
                accounts = registry.Accounts.Select(a => new { key = a.Key, name = a.Name, id = a.Id, source = a.Source, active = a.Key == registry.Active, playing = GameProcess != null && a.Key == playingKey, masteryRank = a.Profile?.MasteryRank ?? snapshots.Current(a.Id ?? a.Key)?.MasteryRank ?? a.Badge, masteryRankMax = a.Profile?.MasteryRank == null && snapshots.Current(a.Id ?? a.Key)?.MasteryRank == null ? a.BadgeMax : null,
                    profile = a.Profile?.Fetched, profileError = a.ProfileError, inventory = snapshots.Current(a.Id ?? a.Key)?.SourceTimestamp, seen = a.Seen, evidence = a.Evidence }),
                masteryRank = profile?.MasteryRank ?? inv?.MasteryRank ?? account?.Badge, masteryRankMax = profile?.MasteryRank == null && inv?.MasteryRank == null ? account?.BadgeMax : null, mastered, total = Catalog.Items.Count, mastery = view.MasteryProgress(), currencies = inv?.Currencies,
                foundry = view.PendingFoundry(), collection = view.Collection(), pinned = view.Pinned(), tasks = new FarmingPlanner(view).Board(true), session = History.Read(registry.Active, Started)["session"], sources,
                events = events.TakeLast(15), logLines = Array.Empty<string>() });
        }
    }
    public JsonNode Get(string path, IReadOnlyDictionary<string, string> query)
    {
        if (path == "state") return Summary(); if (path == "settings") return GetSettings();
        var view = View(); var farming = new FarmingPlanner(view); string Q(string key) => query.GetValueOrDefault(key) ?? "";
        return path switch
        {
            "mastery" => view.Mastery(), "primes" => view.Primes(), "inventory" => view.InventoryRows(),
            "foundry" => J.Node(new { pending = view.PendingFoundry(), items = J.A(view.Craftable()).Concat(J.A(view.Crafts())) }),
            "relics" => view.Relics(Q("owned") == "1"), "trade" => J.Node(new { rows = view.Trade(), source = view.Inventory == null ? "manual" : "inventory" }),
            "find" => view.Find(Q("q")), "find-task" => farming.Find(Q("q")), "tasks" => farming.Board(),
            "task" => farming.Detail(Q("uid"), World.OpenEras) ?? throw new InventoryException("Unknown farming task."),
            "item" => view.Detail(Q("key"), World.OpenEras) ?? throw new InventoryException("Unknown item."),
            "search" => Market.Search(Q("q")), "world" => WorldSummary(view), "fissures" => World.Fissures(view, GetSettings()["fissure_watch"]),
            "rivens" => Rivens.Rows(Catalog), "riven" => RivenService.Table(Catalog, Q("weapon"), ParseCount(Q("pos"), 3), ParseCount(Q("neg"), 1)),
            "history" => History.Read(registry.Active, Started),
            "rewards" => rewards.DeepClone(), "riven/result" => riven.DeepClone(), "hotkeys" => HotkeyStatus?.Invoke() ?? J.Node(new { compositor = (string?)null, lines = Array.Empty<string>(), note = "Open the desktop app to register global hotkeys." }),
            "standing" => Standing(view), "syndicate" => new StandingService(view).Detail(Q("tag"), view.Profile?.MasteryRank ?? view.Inventory?.MasteryRank) ?? throw new InventoryException("Unknown syndicate."),
            _ => throw new NotSupportedException("This service is not attached: " + path)
        };
    }
    private JsonNode Standing(GameView view)
    {
        var row = new StandingService(view).Summary(view.Profile?.MasteryRank ?? view.Inventory?.MasteryRank, World.NightwaveTag);
        lock (sync) row["account"] = Active?.Name;
        return row;
    }
    private JsonNode WorldSummary(GameView view)
    {
        var world = World.Summary(Catalog); world["arbitration"] = Arbitrations.Summary(Catalog, J.Now);
        lock (sync) world["duviri"] = Active?.Duviri is { } offers ? WorldService.Duviri(view, offers, World.SpiralBegan) : null;
        if (world["nightwave"] is JsonObject nightwave) nightwave["mine"] = J.A(new StandingService(view).Summary(view.Profile?.MasteryRank ?? view.Inventory?.MasteryRank, World.NightwaveTag), "syndicates").FirstOrDefault(s => J.S(s, "kind") == "nightwave")?.DeepClone(); return world;
    }
    private void SaveRegistry(RegistryState next) { store.Write("registry", next); registry = next; }
    private void EditAccount(Action<AccountState> edit)
    {
        lock (sync)
        {
            var next = JsonSerializer.Deserialize<RegistryState>(JsonSerializer.Serialize(registry, J.Options), J.Options)!;
            var account = next.Accounts.FirstOrDefault(a => a.Key == next.Active);
            if (account == null) { account = new() { Key = "account-" + Guid.NewGuid().ToString("N"), Name = "Account 1", Source = "local" }; next.Accounts.Add(account); next.Active = account.Key; }
            edit(account); SaveRegistry(next);
        }
    }
    public async Task<JsonNode> Post(string path, JsonNode body, CancellationToken cancellation)
    {
        string Uid() => J.S(body, "uid") ?? throw new InventoryException("uid is required.");
        switch (path)
        {
            case "settings":
                MarketService? oldMarket = null;
                lock (sync)
                {
                    var updated = Settings.Patch(settings, body);
                    if (J.S(updated, "platform") != J.S(settings, "platform"))
                    {
                        var platform = J.S(updated, "platform")!; var nextMarket = new MarketService(Path.Combine(DataDirectory, "cache"), platform, Offline);
                        store.Write("settings", updated); oldMarket = Market; Market = nextMarket; World = new(cache, platform); Rivens.SetPlatform(platform);
                    }
                    if (oldMarket == null) store.Write("settings", updated); settings = updated;
                }
                if (oldMarket != null) { await oldMarket.DisposeAsync(); try { await Market.Load(cancellation); } catch (Exception e) when (e is IOException or HttpRequestException or JsonException) { Event("Market metadata is unavailable on the selected platform.", "error"); } await World.Refresh(cancellation); await Rivens.Refresh(J.S(GetSettings(), "platform")!, cancellation); }
                if (J.Get(body, "hotkey_rewards") != null || J.Get(body, "hotkey_riven") != null) { try { if (OperatingSystem.IsWindows()) RegisterHotkeys?.Invoke(); } catch (InventoryException e) { Event(e.Message, "error"); } }
                break;
            case "account/set":
                var id = (J.S(body, "id") ?? "").Trim(); if (!Regex.IsMatch(id, "^[a-fA-F0-9]{24}$")) throw new InventoryException("Enter a 24-character Warframe account ID.");
                id = id.ToLowerInvariant();
                lock (sync)
                {
                    var next = JsonSerializer.Deserialize<RegistryState>(JsonSerializer.Serialize(registry, J.Options), J.Options)!;
                    var requested = J.S(body, "key") ?? next.Active; var account = next.Accounts.FirstOrDefault(a => a.Key == requested);
                    if (account == null && requested != null) throw new InventoryException("Unknown account.");
                    if (account == null) { account = new() { Key = "account-" + Guid.NewGuid().ToString("N"), Name = "Account " + (next.Accounts.Count + 1) }; next.Accounts.Add(account); next.Active = account.Key; }
                    if (next.Accounts.Any(a => a.Key != account.Key && a.Id == id)) throw new InventoryException("That ID is already linked to another account.");
                    if (account.Id != null && account.Id != id || account.Profile?.Id != null && account.Profile.Id != id) throw new InventoryException("This account is already associated with another ID.");
                    if (snapshots.Current(account.Key) is { } saved && snapshots.Current(id) == null) snapshots.Commit(saved with { AccountId = id, IdentityVerified = false });
                    account.Id = id; account.Source = "entered by you"; SaveRegistry(next);
                }
                break;
            case "account/switch":
                lock (sync) { var key = J.S(body, "key"); if (!registry.Accounts.Any(a => a.Key == key)) throw new InventoryException("Unknown account."); var next = new RegistryState { Active = key, Accounts = registry.Accounts }; SaveRegistry(next); } break;
            case "account/forget":
                lock (sync) { var key = J.S(body, "key"); var accounts = registry.Accounts.Where(a => a.Key != key).ToList(); SaveRegistry(new() { Accounts = accounts, Active = registry.Active == key ? accounts.FirstOrDefault()?.Key : registry.Active }); } break;
            case "profile/import":
                var profile = StateStore.ParseProfile(body);
                EditAccount(a => {
                    if (a.Id != null && profile.Id != null && a.Id != profile.Id) throw new InventoryException("Profile belongs to a different account.");
                    if (profile.Id != null && registry.Accounts.Any(other => other.Key != a.Key && other.Id == profile.Id)) throw new InventoryException("That ID is already linked to another account.");
                    if (a.Id == null && profile.Id != null && snapshots.Current(a.Key) is { } saved && snapshots.Current(profile.Id) == null) snapshots.Commit(saved with { AccountId = profile.Id, IdentityVerified = false });
                    a.Id ??= profile.Id; a.Name = profile.Name ?? a.Name; a.Profile = profile; a.ProfileError = null;
                }); break;
            case "profile/refresh": await RefreshProfile(cancellation); break;
            case "inventory/import":
                string accountId; lock (sync) { if (Active == null) EditAccount(_ => { }); accountId = Active!.Id ?? Active.Key; }
                var snapshot = InventoryParser.Parse(JsonSerializer.SerializeToUtf8Bytes(body), accountId, complete: false);
                var payload = body["InventoryJson"] ?? body;
                if (payload is JsonValue val && val.TryGetValue<string>(out var raw)) payload = JsonNode.Parse(raw)!;
                var complete = new[] { "MiscItems", "Suits", "Recipes", "XPInfo" }.All(k => payload[k] is JsonArray);
                var result = snapshots.Commit(snapshot with { Complete = complete, Provider = "imported file", Warnings = complete ? snapshot.Warnings.Where(w => !w.StartsWith("Partial observation", StringComparison.Ordinal)).ToArray() : snapshot.Warnings }, cancellation);
                Event(result.Reason, result.Promoted ? "info" : "error");
                return J.Node(new { ok = true, promoted = result.Promoted, message = result.Reason });
            case "inventory/clear": lock (sync) { if (Active is { } active) snapshots.ClearCurrent(active.Id ?? active.Key); } break;
            case "riven/grade": return RivenService.GradeBody(Catalog, body);
            case "riven/read-fingerprint":
                var directRiven = FingerprintRivenReader.Read(Catalog, body); directRiven["traded"] = Rivens.Traded(J.S(directRiven, "weapon")!);
                lock (sync) riven = directRiven.DeepClone(); return directRiven;
            case "rewards/scan": return await ScanRewards(false, cancellation);
            case "riven/scan": return await ScanRiven(cancellation);
            case "overlay/test": await TestOverlay(cancellation); break;
            case "hotkeys/register": return RegisterHotkeys?.Invoke() ?? throw new InventoryException("Open the desktop app to register hotkeys.");
            case "manual/count":
                var uid = Uid(); var count = J.Get(body, "count"); if (count != null && J.N(count, -1) < 0) throw new InventoryException("Count must be a nonnegative integer.");
                EditAccount(a => { if (count == null) a.Manual.Counts.Remove(uid); else a.Manual.Counts[uid] = new(J.N(count), J.Now); }); break;
            case "manual/mastered": var masteredUid = Uid(); EditAccount(a => { if (J.B(J.Get(body, "mastered"))) a.Manual.Mastered[masteredUid] = J.Now; else a.Manual.Mastered.Remove(masteredUid); }); break;
            case "manual/pin": var pinUid = Uid(); EditAccount(a => { a.Manual.Pinned.Remove(pinUid); if (J.B(J.Get(body, "pinned"))) a.Manual.Pinned.Add(pinUid); }); break;
            case "manual/task":
                var taskUid = Uid(); if (new FarmingPlanner(View()).Thing(taskUid) == null) throw new InventoryException("This item cannot be added to the farming board.");
                var target = J.Get(body, "target"); if (target != null && J.N(target, -1) < 1) throw new InventoryException("Target must be a positive integer.");
                EditAccount(a => { var existing = a.Manual.Tasks.FirstOrDefault(t => t.Uid == taskUid); var wanted = J.Get(body, "onBoard") == null ? existing != null : J.B(J.Get(body, "onBoard"));
                    var updated = new FarmingTask(taskUid, body is JsonObject obj && obj.ContainsKey("target") ? target == null ? null : J.N(target) : existing?.Target, existing?.Added ?? J.Now);
                    if (!wanted) a.Manual.Tasks.RemoveAll(t => t.Uid == taskUid); else if (existing == null) a.Manual.Tasks.Add(updated); else a.Manual.Tasks[a.Manual.Tasks.IndexOf(existing)] = updated; }); break;
            case "tasks/clear": var view = View(); var planner = new FarmingPlanner(view); var done = view.Manual.Tasks.Where(t => J.S(planner.Row(t), "state") == "done").Select(t => t.Uid).ToHashSet(); EditAccount(a => a.Manual.Tasks.RemoveAll(t => done.Contains(t.Uid))); return J.Node(new { removed = done.Count });
            case "data/reload": await Reload(cancellation, true); if (Catalog.LoadedAt == null) throw new InventoryException(DataError ?? "Catalog is unavailable."); break;
            case "window/show": ShowRequested?.Invoke(); break;
            case "quit": QuitRequested?.Invoke(); break;
            default: throw new NotSupportedException("This service is not attached: " + path);
        }
        return J.Node(new { ok = true });
    }
    private async Task RefreshProfile(CancellationToken cancellation)
    {
        if (Offline) throw new InventoryException("Offline mode is enabled.");
        string key, id; lock (sync) { key = Active?.Key ?? throw new InventoryException("Add an account first."); id = Active?.Id ?? throw new InventoryException("Enter an account ID first."); }
        await profileGate.WaitAsync(cancellation);
        try
        {
            if (DateTimeOffset.UtcNow < nextProfile) throw new InventoryException("Please wait before requesting another public profile.");
            nextProfile = DateTimeOffset.UtcNow.AddSeconds(300);
            var host = J.S(GetSettings(), "platform") switch { "ps4" => "api-ps4", "xb1" => "api-xb1", "swi" => "api-swi", _ => "api" };
            using var response = await profileClient.GetAsync($"https://{host}.warframe.com/cdn/getProfileViewingData.php?playerId={Uri.EscapeDataString(id)}", cancellation);
            if (!response.IsSuccessStatusCode) throw new InventoryException("Profile service refused the request. Wait before retrying, or import a saved profile.");
            var parsed = StateStore.ParseProfile(JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation)) ?? throw new InventoryException("Profile service returned no data."));
            if (parsed.Id != null && parsed.Id != id) throw new InventoryException("Profile service returned another account.");
            lock (sync)
            {
                var next = JsonSerializer.Deserialize<RegistryState>(JsonSerializer.Serialize(registry, J.Options), J.Options)!; var account = next.Accounts.FirstOrDefault(a => a.Key == key && a.Id == id);
                if (account != null) { account.Profile = parsed with { Source = "DE profile" }; account.Name = parsed.Name ?? account.Name; account.ProfileError = null; SaveRegistry(next); }
            }
        }
        finally { profileGate.Release(); }
    }
    private static int ParseCount(string value, int fallback) => value.Length == 0 ? fallback : int.TryParse(value, out var count) && count >= 0 ? count : throw new InventoryException("Riven count must be a nonnegative integer.");
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); if (background != null) { try { await background; } catch (OperationCanceledException) { } }
        if (gameLoop != null) { try { await gameLoop; } catch (OperationCanceledException) { } }
        if (memoryScan != null) { try { await memoryScan; } catch (OperationCanceledException) { } }
        if (automaticOcr != null) { try { await automaticOcr; } catch (OperationCanceledException) { } }
        RewardScreenOpened -= StartAutomaticOcr; RewardScreenClosed -= CloseRewards;
        await Market.DisposeAsync(); Art.Dispose(); cache.Dispose(); profileClient.Dispose(); stop.Dispose(); reload.Dispose(); profileGate.Dispose();
    }
}
