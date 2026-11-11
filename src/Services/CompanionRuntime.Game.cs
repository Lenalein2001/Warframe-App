using System.Text.Json;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed partial class CompanionRuntime
{
    public GameLog Log { get; } = new();
    public int? GameProcess { get; private set; }
    public bool ScanRunning { get; private set; }
    public string? ScanError { get; private set; }
    private JsonNode? lastSuccessfulScan;
    private Task? gameLoop;
    private Task? memoryScan;
    private CancellationTokenSource? memoryCancellation;
    private Task? automaticProfile;
    private double nextMemory;
    private double lastMemory;
    private double nextAutoProfile;
    private bool memoryRequested;
    private string? playingKey;
    private long gameSession;
    private (string? Path, DateTime Time, long Length) watchedInventory;
    private readonly List<JsonNode> duviri = [];
    private void RepairAccountNames()
    {
        var original = JsonSerializer.Serialize(registry, J.Options);
        foreach (var account in registry.Accounts)
        {
            account.Name = GameLog.PlayerName(account.Name);
            if (account.Profile is { } profile) account.Profile = profile with { Name = GameLog.PlayerName(profile.Name) };
        }
        foreach (var duplicate in registry.Accounts.Where(a => a.Id == null && a.Profile == null &&
            a.Manual.Counts.Count == 0 && a.Manual.Mastered.Count == 0 && a.Manual.Pinned.Count == 0 && a.Manual.Tasks.Count == 0 &&
            snapshots.Current(a.Key) == null).ToArray())
        {
            var matches = registry.Accounts.Where(a => a.Id != null && !string.IsNullOrWhiteSpace(a.Name) &&
                string.Equals(a.Name, duplicate.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) continue;
            var linked = matches[0];
            linked.Seen = Math.Max(linked.Seen ?? 0, duplicate.Seen ?? 0);
            linked.Duviri ??= duplicate.Duviri;
            linked.ExpectedMemorySections = linked.ExpectedMemorySections.Union(duplicate.ExpectedMemorySections).ToArray();
            linked.Evidence = linked.Evidence.Concat(duplicate.Evidence).DistinctBy(e => e.Id).Take(12).ToList();
            if (registry.Active == duplicate.Key) registry.Active = linked.Key;
            registry.Accounts.Remove(duplicate);
        }
        if (JsonSerializer.Serialize(registry, J.Options) == original) return;
        if (store.Read<JsonNode>("registry-before-name-repair") == null)
            store.Write("registry-before-name-repair", JsonNode.Parse(original));
        store.Write("registry", registry);
    }
    public event Action? RewardScreenOpened;
    public event Action? RewardScreenClosed;
    private void LinkLogAccount(AccountState account, string id)
    {
        if (account.Id != null && account.Id != id) return;
        if (snapshots.Current(account.Key) is { } saved && snapshots.Current(id) == null)
            snapshots.Commit(saved with { AccountId = id, IdentityVerified = false });
        account.Id = id;
        account.Source = "EE.log";
    }
    private void ResolveLogIdentity(string id)
    {
        var pending = registry.Accounts.FirstOrDefault(a => a.Key == playingKey);
        if (pending == null || pending.Id != null) return;
        var known = registry.Accounts.FirstOrDefault(a => a.Id == id);
        if (known != null && known.Key != pending.Key)
        {
            // An explicit ID outranks a provisional name match. Retain both profiles'
            // saved data, and follow the game only if the provisional profile was shown.
            if (registry.Active == pending.Key) registry.Active = known.Key;
            playingKey = known.Key;
            known.Name = GameLog.PlayerName(pending.Name) ?? known.Name;
            RepairAccountNames();
            gameSession++; memoryCancellation?.Cancel();
            Interlocked.Increment(ref rewardGeneration);
            lastSuccessfulScan = null; ScanError = null; memoryRequested = true;
            SaveRegistry(registry);
        }
        else
        {
            LinkLogAccount(pending, id); gameSession++; memoryCancellation?.Cancel(); memoryRequested = true; SaveRegistry(registry);
        }
    }
    private void LogSignal(LogSignal signal)
    {
        lock (sync)
        {
            if (signal.Kind == "login")
            {
                gameSession++; memoryCancellation?.Cancel();
                var next = JsonSerializer.Deserialize<RegistryState>(JsonSerializer.Serialize(registry, J.Options), J.Options)!;
                var matches = next.Accounts.Where(a => string.Equals(GameLog.PlayerName(a.Name), GameLog.PlayerName(signal.Value), StringComparison.OrdinalIgnoreCase) && (signal.Id == null || a.Id == null || a.Id == signal.Id)).ToArray();
                var provisional = matches.Where(a => a.Id == null).ToArray();
                var account = next.Accounts.FirstOrDefault(a => signal.Id != null && a.Id == signal.Id)
                    ?? (matches.Length == 1 ? matches[0] : provisional.Length == 1 ? provisional[0] : null);
                if (account == null) { account = new() { Key = "account-" + Guid.NewGuid().ToString("N"), Name = signal.Value, Id = signal.Id, Source = "EE.log" }; next.Accounts.Add(account); }
                account.Seen = signal.Time ?? account.Seen; if (signal.Id != null) LinkLogAccount(account, signal.Id);
                if (playingKey != account.Key) { Interlocked.Increment(ref rewardGeneration); lastSuccessfulScan = null; ScanError = null; }
                playingKey = account.Key;
                if (!signal.Historical || next.Active == null || (signal.Time ?? 0) > next.Switched) { next.Active = account.Key; next.Switched = signal.Time ?? J.Now; }
                SaveRegistry(next); memoryRequested = true;
            }
            else if (signal.Kind == "identity" && playingKey != null)
            {
                if (signal.Id != null) ResolveLogIdentity(signal.Id);
            }
            else if (signal.Kind == "candidate" && playingKey != null && signal.Id != null)
            {
                var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey);
                if (signal.Sure) ResolveLogIdentity(signal.Id);
                else if (account != null && !registry.Accounts.Any(a => a.Id == signal.Id))
                {
                    if (account.Evidence.All(e => e.Id != signal.Id))
                    {
                        account.Evidence.Add(new(signal.Id, signal.Value ?? "Game log account candidate."));
                        if (account.Evidence.Count > 12) account.Evidence.RemoveRange(0, account.Evidence.Count - 12);
                    }
                    SaveRegistry(registry);
                }
            }
            else if (signal.Kind == "badge" && playingKey != null)
            { var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey); if (account != null) { account.Badge = signal.Rank; account.BadgeMax = signal.RankMax; SaveRegistry(registry); } }
            else if (signal.Kind == "sync") memoryRequested = true;
            else if (signal.Kind == "duviri-start") duviri.Clear();
            else if (signal.Kind == "duviri-item") duviri.Add(J.Node(new { uid = signal.Value, owned = signal.Id == "owned", category = signal.Rank }));
            else if (signal.Kind == "duviri-done" && playingKey != null)
            { var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey); if (account != null) { account.Duviri = J.Node(new { rolled = signal.Time, offers = duviri }); SaveRegistry(registry); } }
        }
        if (signal.Kind == "rewards") RewardScreenOpened?.Invoke();
        if (signal.Kind == "rewards-closed") RewardScreenClosed?.Invoke();
    }
    internal Func<int?> FindGameProcess { get; set; } = MemoryProvider.GamePid;
    private async Task GameLoop()
    {
        Log.Signal += LogSignal;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var config = GetSettings(); var detected = FindGameProcess();
                lock (sync)
                {
                    if (GameProcess != detected)
                    {
                        if (GameProcess != null || detected == null) playingKey = null;
                        GameProcess = detected; gameSession++; memoryCancellation?.Cancel(); memoryRequested = detected != null;
                        lastSuccessfulScan = null; ScanError = null;
                    }
                }
                Log.Poll(J.S(config, "ee_log_path"));
                if (J.S(config, "inventory_json_path") is { } file && File.Exists(file))
                {
                    try
                    {
                        var info = new FileInfo(file); var stamp = (file, info.LastWriteTimeUtc, info.Length);
                        if (watchedInventory != stamp && info.Length <= InventoryParser.MaxBytes)
                        {
                            // Set only after a successful parse, so a file being rewritten is retried.
                            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, true);
                            await ImportWatchedInventory(input, stop.Token); watchedInventory = stamp;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InventoryException or JsonException)
                    { Event("Inventory file import: " + (e is InventoryException ? e.Message : e.GetType().Name), "error"); }
                }
                if (J.B(config["memory_scan_enabled"]) && GameProcess is { } pid && (memoryScan == null || memoryScan.IsCompleted) && (memoryRequested || J.Now >= nextMemory) && J.Now >= lastMemory + 10)
                {
                    if (memoryScan != null) await memoryScan;
                    memoryRequested = false; lastMemory = J.Now; nextMemory = J.Now + 300;
                    memoryScan = CaptureMemory(pid, stop.Token);
                }
                if (!Offline && J.B(config["profile_auto_refresh"]) && J.Now >= nextAutoProfile && (automaticProfile == null || automaticProfile.IsCompleted))
                {
                    lock (sync)
                    {
                        // Waiting for account detection must not consume the refresh interval.
                        if (Active?.Id != null)
                        {
                            nextAutoProfile = J.Now + Math.Max(1, J.N(config, "profile_refresh_minutes", 30)) * 60;
                            automaticProfile = RefreshAutomaticProfile();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InventoryException or JsonException or HttpRequestException)
            { Event("Game integration: " + (e is InventoryException ? e.Message : e.GetType().Name), "error"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stop.Token); } catch (OperationCanceledException) { break; }
        }
        Log.Signal -= LogSignal;
    }
    internal async Task<bool> ImportWatchedInventory(Stream input, CancellationToken cancellation)
    {
        string? key, identity; long session;
        lock (sync) { key = Active?.Key; identity = Active?.Id ?? key; session = gameSession; }
        if (identity == null) throw new InventoryException("Waiting for an account before importing the watched inventory file.");
        var bytes = await LimitedRead.Bytes(input, InventoryParser.MaxBytes, cancellation);
        var body = JsonNode.Parse(bytes) ?? throw new InventoryException("Watched inventory file is empty.");
        lock (sync)
        {
            if (Active?.Key != key || (Active?.Id ?? Active?.Key) != identity || gameSession != session)
            { Event("Account changed while reading the inventory file; reading discarded. Update the file to import again.", "info"); return false; }
            ImportInventory(body, identity, cancellation); return true;
        }
    }
    private async Task RefreshAutomaticProfile()
    {
        try { await RefreshProfile(stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (OperationCanceledException) { Event("Public profile request timed out; game monitoring will continue.", "error"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InventoryException or JsonException or HttpRequestException)
        { Event("Public profile refresh: " + (e is InventoryException ? e.Message : e.GetType().Name), "error"); }
    }
    internal Func<int, string, CancellationToken, Func<MemoryCapture, bool>, MemoryCapture> ReadMemory { get; set; } = (pid, account, cancellation, acceptable) => MemoryProvider.Scan(pid, account, cancellation, acceptable);
    internal async Task CaptureMemory(int pid, CancellationToken cancellation)
    {
        string key, identity; long session; Dictionary<string, long> known; string[] migratedSections;
        lock (sync)
        {
            var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey);
            if (!J.B(settings["memory_scan_enabled"])) return;
            if (account == null) { ScanError = "Waiting for the game log to identify the logged-in account."; return; }
            key = account.Key; identity = account.Id ?? key; session = gameSession; known = account.Profile?.Xp ?? snapshots.Current(identity)?.Xp ?? [];
            migratedSections = account.ExpectedMemorySections;
            memoryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cancellation = memoryCancellation.Token;
        }
        ScanRunning = true;
        try
        {
            var baseline = (store.Read<string[]>("memory-sections-" + key) ?? []).Union(migratedSections).ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool Accept(MemoryCapture reading) => (reading.Snapshot.IdentityVerified || LegacyMigration.SameOwner(reading.Snapshot.Xp, known) == true) && new[] { "MiscItems", "Suits", "Recipes", "XPInfo" }.Concat(baseline).All(reading.Sections.Contains);
            var capture = await Task.Run(() => ReadMemory(pid, identity, cancellation, Accept), cancellation);
            var owns = capture.Snapshot.IdentityVerified || LegacyMigration.SameOwner(capture.Snapshot.Xp, known) == true;
            var complete = Accept(capture);
            lock (sync)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!J.B(settings["memory_scan_enabled"])) return;
                var currentAccount = registry.Accounts.FirstOrDefault(a => a.Key == key);
                if (playingKey != key || GameProcess != pid || gameSession != session || (currentAccount?.Id ?? currentAccount?.Key) != identity)
                { ScanError = "Game session or account identity changed during capture; reading discarded."; memoryRequested = true; return; }
                var result = snapshots.Commit(capture.Snapshot with { Complete = complete, IdentityVerified = owns }, cancellation);
                if (result.Promoted) store.Write("memory-sections-" + key, baseline.Union(capture.Sections).ToArray());
                if (complete) lastSuccessfulScan = J.Node(new { t = J.Now, stacks = capture.Snapshot.Counts.Count, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) });
                ScanError = complete ? null : owns ? "Partial capture retained; confirmed inventory unchanged." : "Capture ownership is unverified; refresh the public profile before capturing.";
                Event(result.Reason, result.Promoted ? "info" : "error");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or InventoryException or UnauthorizedAccessException)
        { ScanError = e is InventoryException ? e.Message : "Game memory was not readable; confirmed inventory retained."; }
        finally { lock (sync) { ScanRunning = false; memoryCancellation?.Dispose(); memoryCancellation = null; } }
    }
}
