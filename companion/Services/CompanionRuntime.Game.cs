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
    private Task? gameLoop;
    private Task? memoryScan;
    private double nextMemory;
    private double lastMemory;
    private double nextAutoProfile;
    private bool memoryRequested;
    private string? playingKey;
    private (string? Path, DateTime Time, long Length) watchedInventory;
    private readonly List<JsonNode> duviri = [];
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
    private void LogSignal(LogSignal signal)
    {
        lock (sync)
        {
            if (signal.Kind == "login")
            {
                var next = JsonSerializer.Deserialize<RegistryState>(JsonSerializer.Serialize(registry, J.Options), J.Options)!;
                var account = next.Accounts.FirstOrDefault(a => signal.Id != null && a.Id == signal.Id) ?? next.Accounts.FirstOrDefault(a => string.Equals(a.Name, signal.Value, StringComparison.OrdinalIgnoreCase) && (signal.Id == null || a.Id == null || a.Id == signal.Id));
                if (account == null) { account = new() { Key = "account-" + Guid.NewGuid().ToString("N"), Name = signal.Value, Id = signal.Id, Source = "EE.log" }; next.Accounts.Add(account); }
                account.Seen = signal.Time ?? account.Seen; if (signal.Id != null) LinkLogAccount(account, signal.Id);
                playingKey = account.Key; next.Active = account.Key; SaveRegistry(next); memoryRequested = true;
            }
            else if (signal.Kind == "identity" && playingKey != null)
            {
                var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey);
                if (account?.Id == null && account != null && signal.Id != null && !registry.Accounts.Any(a => a.Id == signal.Id)) { LinkLogAccount(account, signal.Id); SaveRegistry(registry); }
            }
            else if (signal.Kind == "candidate" && playingKey != null && signal.Id != null)
            {
                var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey);
                if (account != null && !registry.Accounts.Any(a => a.Id == signal.Id))
                {
                    if (signal.Sure && account.Id == null) LinkLogAccount(account, signal.Id);
                    else if (account.Evidence.All(e => e.Id != signal.Id))
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
    private async Task GameLoop()
    {
        Log.Signal += LogSignal;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var config = GetSettings(); GameProcess = MemoryProvider.GamePid(); Log.Poll(J.S(config, "ee_log_path"));
                if (J.S(config, "inventory_json_path") is { } file && File.Exists(file))
                {
                    var info = new FileInfo(file); var stamp = (file, info.LastWriteTimeUtc, info.Length);
                    if (watchedInventory != stamp && info.Length <= InventoryParser.MaxBytes)
                    {
                        // Set only after a successful parse, so a file being rewritten is retried.
                        var body = JsonNode.Parse(await File.ReadAllTextAsync(file, stop.Token)) ?? throw new InventoryException("Watched inventory file is empty.");
                        await Post("inventory/import", body, stop.Token); watchedInventory = stamp;
                    }
                }
                if (J.B(config["memory_scan_enabled"]) && GameProcess is { } pid && (memoryScan == null || memoryScan.IsCompleted) && (memoryRequested || J.Now >= nextMemory) && J.Now >= lastMemory + 10)
                {
                    if (memoryScan != null) await memoryScan;
                    memoryRequested = false; lastMemory = J.Now; nextMemory = J.Now + 300;
                    memoryScan = CaptureMemory(pid, stop.Token);
                }
                if (!Offline && J.B(config["profile_auto_refresh"]) && J.Now >= nextAutoProfile)
                {
                    nextAutoProfile = J.Now + Math.Max(1, J.N(config, "profile_refresh_minutes", 30)) * 60;
                    if (Active?.Id != null) await RefreshProfile(stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InventoryException or JsonException or HttpRequestException)
            { Event("Game integration: " + (e is InventoryException ? e.Message : e.GetType().Name), "error"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stop.Token); } catch (OperationCanceledException) { break; }
        }
        Log.Signal -= LogSignal;
    }
    private async Task CaptureMemory(int pid, CancellationToken cancellation)
    {
        string key, identity; Dictionary<string, long> known;
        lock (sync)
        {
            var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey);
            if (account == null) { ScanError = "Waiting for the game log to identify the logged-in account."; return; }
            key = account.Key; identity = account.Id ?? key; known = account.Profile?.Xp ?? snapshots.Current(identity)?.Xp ?? [];
        }
        ScanRunning = true;
        try
        {
            var baseline = store.Read<string[]>("memory-sections-" + key) ?? [];
            bool Accept(MemoryCapture reading) => (reading.Snapshot.IdentityVerified || LegacyMigration.SameOwner(reading.Snapshot.Xp, known) == true) && new[] { "MiscItems", "Suits", "Recipes", "XPInfo" }.Concat(baseline).All(reading.Sections.Contains);
            var capture = await Task.Run(() => MemoryProvider.Scan(pid, identity, cancellation, Accept), cancellation);
            var owns = capture.Snapshot.IdentityVerified || LegacyMigration.SameOwner(capture.Snapshot.Xp, known) == true;
            var complete = Accept(capture);
            lock (sync)
            {
                if (playingKey != key || GameProcess != pid) { ScanError = "Account changed during capture; reading discarded."; return; }
                var result = snapshots.Commit(capture.Snapshot with { Complete = complete, IdentityVerified = owns }, cancellation);
                if (result.Promoted) store.Write("memory-sections-" + key, baseline.Union(capture.Sections).ToArray());
                ScanError = complete ? null : owns ? "Partial capture retained; confirmed inventory unchanged." : "Capture ownership is unverified; refresh the public profile before capturing.";
                Event(result.Reason, result.Promoted ? "info" : "error");
            }
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or InventoryException or UnauthorizedAccessException)
        { ScanError = e is InventoryException ? e.Message : "Game memory was not readable; confirmed inventory retained."; }
        finally { ScanRunning = false; }
    }
}
