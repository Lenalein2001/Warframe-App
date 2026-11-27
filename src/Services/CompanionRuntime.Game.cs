using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;

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
    private enum MemoryTrigger { Login, Mission, Inventory }
    private sealed record PendingMemory(long Session, long Ready, long Expires, MemoryTrigger Trigger, long LastSignal);
    private PendingMemory? pendingMemory;
    private sealed record MissionRead(long Session, long Started, MemoryTrigger Trigger)
    {
        public bool Covers(long session, long signalTime, MemoryTrigger trigger) =>
            session == Session && signalTime < Started + 15000 && trigger <= Trigger;
    }
    private MissionRead? confirmedMissionRead;
    private long nextAutomaticMemory;
    private Task? automaticProfile;
    private double nextAutoProfile;
    private string? playingKey;
    private long gameSession;
    private (string? Path, DateTime Time, long Length, (ulong, ulong) Identity) watchedInventory;
    private (string? Path, string? Hash) watchedContent;
    internal TimeSpan GamePollInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan InventoryVerificationInterval { get; set; } = TimeSpan.FromMinutes(5);
    internal bool FileNotificationsEnabled { get; set; } = true;
    private Dictionary<(string Uid, int? Category), JsonNode>? duviri;
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
            a.Manual.KeepCopies.Count == 0 && snapshots.Current(a.Key) == null && !snapshots.HasPending(a.Key)).ToArray())
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
        if (account.Id == null) snapshots.SeedLinkedAccount(account.Key, id);
        account.Id = id;
        account.Source = "EE.log";
    }
    private void ResolveLogIdentity(string id)
    {
        var pending = registry.Accounts.FirstOrDefault(a => a.Key == playingKey);
        if (pending == null || pending.Id != null) return;
        var liveRequest = pendingMemory;
        pendingMemory = null;
        var previousSession = gameSession;
        var known = registry.Accounts.FirstOrDefault(a => a.Id == id);
        if (known != null && known.Key != pending.Key)
        {
            // An explicit ID outranks a provisional name match. Retain both profiles'
            // saved data, and follow the game only if the provisional profile was shown.
            if (registry.Active == pending.Key) registry.Active = known.Key;
            playingKey = known.Key;
            duviri = null;
            rewardScreenId = null;
            known.Name = GameLog.PlayerName(pending.Name) ?? known.Name;
            RepairAccountNames();
            gameSession++; memoryCancellation?.Cancel();
            Interlocked.Increment(ref rewardGeneration);
            lastSuccessfulScan = null; ScanError = null;
            SaveRegistry(registry);
        }
        else
        {
            LinkLogAccount(pending, id); gameSession++; memoryCancellation?.Cancel(); SaveRegistry(registry);
        }
        if (liveRequest is { } request && request.Session == previousSession && Active?.Key == playingKey)
            pendingMemory = request with { Session = gameSession };
    }
    private void LogSignal(LogSignal signal)
    {
        lock (sync)
        {
            if (signal.Kind == "login")
            {
                if (signal.NameNotice && registry.Accounts.FirstOrDefault(a => a.Key == playingKey) is { } playing &&
                    string.Equals(playing.Name, signal.Value, StringComparison.OrdinalIgnoreCase) &&
                    (signal.Id == null || signal.Id == playing.Id))
                    return;
                gameSession++; memoryCancellation?.Cancel();
                pendingMemory = null;
                duviri = null;
                rewardScreenId = null;
                var next = JsonSerializer.Deserialize<RegistryState>(JsonSerializer.Serialize(registry, J.Options), J.Options)!;
                var matches = next.Accounts.Where(a => string.Equals(GameLog.PlayerName(a.Name), GameLog.PlayerName(signal.Value), StringComparison.OrdinalIgnoreCase) && (signal.Id == null || a.Id == null || a.Id == signal.Id)).ToArray();
                var provisional = matches.Where(a => a.Id == null).ToArray();
                var account = next.Accounts.FirstOrDefault(a => signal.Id != null && a.Id == signal.Id)
                    ?? (matches.Length == 1 ? matches[0] : provisional.Length == 1 ? provisional[0] : null);
                if (account == null) { account = new() { Key = "account-" + Guid.NewGuid().ToString("N"), Name = signal.Value, Id = signal.Id, Source = "EE.log" }; next.Accounts.Add(account); }
                if (signal.Id != null)
                {
                    account.Name = signal.Value;
                    LinkLogAccount(account, signal.Id);
                }
                account.Seen = signal.Time ?? account.Seen;
                if (playingKey != account.Key) { Interlocked.Increment(ref rewardGeneration); lastSuccessfulScan = null; ScanError = null; }
                playingKey = account.Key;
                if (!signal.Historical || next.Active == null || (signal.Time ?? 0) > next.Switched) { next.Active = account.Key; next.Switched = signal.Time ?? J.Now; }
                SaveRegistry(next);
                if (!signal.Historical) QueueMemoryRead(MemoryTrigger.Login);
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
                        SaveRegistry(registry);
                    }
                }
            }
            else if (signal.Kind == "badge" && playingKey != null)
            { var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey); if (account != null && (account.Badge != signal.Rank || account.BadgeMax != signal.RankMax)) { account.Badge = signal.Rank; account.BadgeMax = signal.RankMax; SaveRegistry(registry); } }
            else if (signal.Kind == "sync" && !signal.Historical)
                QueueMemoryRead(signal.Value == "inventory" ? MemoryTrigger.Inventory : MemoryTrigger.Mission);
            else if (signal.Kind == "rewards")
            {
                var at = signal.Time ?? J.Now;
                if (rewardScreenId == null || signal.Value == "open" && at - rewardOpenedAt >= 20)
                {
                    if (rewardScreenId != null) Interlocked.Increment(ref rewardGeneration);
                    rewardOpenedAt = at;
                    rewardScreenId = Guid.NewGuid().ToString("N");
                }
            }
            else if (signal.Kind == "rewards-closed") rewardScreenId = null;
            else if (signal.Kind == "duviri-start") duviri = playingKey == null ? null : [];
            else if (signal.Kind == "duviri-cached") duviri = null;
            else if (signal.Kind == "duviri-item" && duviri != null && signal.Value is { } uid)
            {
                if (duviri.Count >= 128) duviri = null;
                else duviri[(uid, signal.Rank)] = J.Node(new { uid, owned = signal.Id == "owned", category = signal.Rank });
            }
            else if (signal.Kind == "duviri-done")
            {
                var roll = duviri;
                duviri = null;
                var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey);
                if (account != null && roll?.Count > 0 && (J.D(account.Duviri?["rolled"]) is not { } saved || signal.Time >= saved))
                {
                    var next = J.Node(new { rolled = signal.Time, offers = roll.Values });
                    if (!JsonNode.DeepEquals(account.Duviri, next)) { account.Duviri = next; SaveRegistry(registry); }
                }
            }
        }
        if (signal.Kind == "rewards") RewardScreenOpened?.Invoke();
        if (signal.Kind == "rewards-closed") RewardScreenClosed?.Invoke();
    }
    private void ResetLogSession()
    {
        lock (sync) {
            playingKey = null; gameSession++; memoryCancellation?.Cancel();
            pendingMemory = null;
            lastSuccessfulScan = null; ScanError = null; duviri = null; rewardScreenId = null;
            Interlocked.Increment(ref rewardGeneration);
        }
    }
    internal Func<int?> FindGameProcess { get; set; } = MemoryProvider.GamePid;
    internal Func<int, DateTimeOffset?> GameStartedAt { get; set; } = static pid =>
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.StartTime.ToUniversalTime(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    };
    private async Task GameLoop()
    {
        Log.Signal += LogSignal; Log.Reset += ResetLogSession;
        using var changes = new FileChangeWake();
        var awaitingRestart = false;
        long nextInventoryPoll = 0, nextContentCheck = 0;
        string? lastPath = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var config = GetSettings(); var detected = FindGameProcess();
                lock (sync)
                {
                    if (GameProcess != detected)
                    {
                        var sameLogin = !awaitingRestart && GameProcess == null && detected is { } pid &&
                            playingKey != null && Log.LoginMatchesProcess(GameStartedAt(pid));
                        GameProcess = detected;
                        awaitingRestart = detected == null;
                        if (!sameLogin)
                        {
                            ResetLogSession();
                            Log.ProcessChanged(detected != null);
                        }
                    }
                }
                var logPath = J.S(config, "ee_log_path");
                if (string.IsNullOrWhiteSpace(logPath)) logPath = GameLog.Discover();
                var inventoryPath = J.S(config, "inventory_json_path");
                if (FileNotificationsEnabled) changes.Watch(logPath, inventoryPath);
                var inventoryChanged = changes.TakeInventoryChange();
                Log.PollPath(logPath, deferReplacement: awaitingRestart);
                if (!Log.ReadingRestartHistory) StartPendingMemoryRead();
                var now = Environment.TickCount64;
                var inspect = inventoryChanged || inventoryPath != lastPath || now >= nextInventoryPoll;
                lastPath = inventoryPath;
                if (inspect) nextInventoryPoll = now + (long)GamePollInterval.TotalMilliseconds;
                if (inspect && inventoryPath is { } file && File.Exists(file))
                {
                    try
                    {
                        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, true);
                        var stamp = (file, File.GetLastWriteTimeUtc(input.SafeFileHandle), input.Length, NativeEngine.FileIdentity(input.SafeFileHandle));
                        if ((inventoryChanged || watchedInventory != stamp || now >= nextContentCheck) && input.Length <= InventoryParser.MaxBytes)
                        {
                            // Set only after a successful parse, so a file being rewritten is retried.
                            nextContentCheck = 0;
                            await ImportWatchedInventory(input, stop.Token, file);
                            watchedInventory = stamp;
                            nextContentCheck = now + (long)InventoryVerificationInterval.TotalMilliseconds;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InventoryException or JsonException)
                    { Event("Inventory file import: " + (e is InventoryException ? e.Message : e.GetType().Name), "error"); }
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
            try { await changes.Wait(GamePollInterval, stop.Token); } catch (OperationCanceledException) { break; }
        }
        Log.Signal -= LogSignal; Log.Reset -= ResetLogSession;
    }
    internal async Task<bool> ImportWatchedInventory(Stream input, CancellationToken cancellation, string? file = null)
    {
        string? key, identity; long session;
        lock (sync) { key = Active?.Key; identity = Active?.Id ?? key; session = gameSession; }
        if (identity == null) throw new InventoryException("Waiting for an account before importing the watched inventory file.");
        if (file != null && watchedContent.Path == file && input is FileStream stream)
        {
            // Check the existing receipt in bounded chunks, including on periodic verification.
            // Hash the bytes actually parsed again if the writer changed the file between reads.
            var position = stream.Position;
            var length = stream.Length - position;
            if (length < 0 || length > InventoryParser.MaxBytes) throw new InventoryException("Inventory exceeds the read limit.");
            using var check = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[16384];
            while (length > 0)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), cancellation);
                if (n == 0) break;
                check.AppendData(buffer.AsSpan(0, n));
                length -= n;
            }
            if (length == 0 && stream.Position == stream.Length && Convert.ToHexString(check.GetHashAndReset()) == watchedContent.Hash) return false;
            stream.Position = position;
        }
        var bytes = await LimitedRead.Bytes(input, InventoryParser.MaxBytes, cancellation);
        var hash = file == null ? null : Convert.ToHexString(SHA256.HashData(bytes));
        if (file != null && watchedContent == (file, hash)) return false;
        var body = JsonNode.Parse(bytes) ?? throw new InventoryException("Watched inventory file is empty.");
        lock (sync)
        {
            if (Active?.Key != key || (Active?.Id ?? Active?.Key) != identity || gameSession != session)
            { if (file != null) watchedContent = (file, hash); Event("Account changed while reading the inventory file; reading discarded. Update the file to import again.", "info"); return false; }
            WatchedExport? export = null;
            if (file != null)
            {
                var path = Path.GetFullPath(file);
                if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
                export = new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))), hash!);
            }
            ImportInventory(body, identity, cancellation, export);
            if (file != null) watchedContent = (file, hash);
            return true;
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
    internal Func<long> MemoryClock { get; set; } = static () => Environment.TickCount64;

    private void QueueMemoryRead(MemoryTrigger trigger)
    {
        if (playingKey == null || Active?.Key != playingKey ||
            !J.B(settings["memory_scan_enabled"])) return;
        var now = MemoryClock();
        // Mission-end and level-exit lines can trail the inventory update. A late
        // inventory update can still supersede a read started by a mission hint.
        if (confirmedMissionRead?.Covers(gameSession, now, trigger) == true) return;

        var ready = now + (trigger == MemoryTrigger.Mission ? 6000 : 2000);
        if (pendingMemory is { } pending && pending.Session == gameSession && now < pending.Expires)
        {
            pendingMemory = trigger > pending.Trigger
                ? new(gameSession, ready, now + 60000, trigger, now)
                : pending with { LastSignal = now };
            return;
        }
        pendingMemory = new(gameSession, ready, now + 60000, trigger, now);
    }

    private void StartPendingMemoryRead()
    {
        lock (sync)
        {
            if (pendingMemory is not { } pending) return;
            var now = MemoryClock();
            if (pending.Session != gameSession || now >= pending.Expires ||
                !J.B(settings["memory_scan_enabled"]) || Active?.Key != playingKey)
            {
                pendingMemory = null;
                return;
            }
            var account = Active;
            if (now < pending.Ready || now < nextAutomaticMemory || memoryScan is { IsCompleted: false } ||
                account?.Id is not { } identity || GameProcess is not { } pid || stop.IsCancellationRequested) return;
            BeginMemoryRead(pid, account, identity, stop.Token);
        }
    }

    private Task CaptureMemory(CancellationToken cancellation)
    {
        lock (sync)
        {
            cancellation.ThrowIfCancellationRequested();
            stop.Token.ThrowIfCancellationRequested();
            if (!J.B(settings["memory_scan_enabled"])) throw new InventoryException("Enable memory reads in Settings first.");
            if (memoryScan is { IsCompleted: false }) throw new InventoryException("An inventory read is already running.");
            if (GameProcess is not { } pid) throw new InventoryException("Start Warframe and log in first.");
            var account = Active;
            if (account?.Id is not { } identity || account.Key != playingKey)
                throw new InventoryException("Select the logged-in account after its ID is detected or linked.");
            return BeginMemoryRead(pid, account, identity, cancellation);
        }
    }

    private Task BeginMemoryRead(int pid, AccountState account, string identity, CancellationToken cancellation)
    {
        var known = account.Profile?.Xp ?? snapshots.Current(identity)?.Xp ?? [];
        var now = MemoryClock();
        MissionRead? missionRead = null;
        if (pendingMemory is { } pending)
        {
            if (pending.Session != gameSession || now >= pending.Expires) pendingMemory = null;
            else if (now >= pending.Ready)
            {
                if (pending.Trigger != MemoryTrigger.Login)
                    missionRead = new(gameSession, now, pending.Trigger);
                pendingMemory = null;
            }
        }
        nextAutomaticMemory = now + 10000;
        memoryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
        ScanRunning = true;
        ScanError = null;
        return memoryScan = CaptureMemory(pid, account.Key, identity, gameSession, known,
            account.ExpectedMemorySections, missionRead, memoryCancellation.Token);
    }

    private async Task CaptureMemory(int pid, string key, string identity, long session, Dictionary<string, long> known, string[] migratedSections, MissionRead? missionRead, CancellationToken cancellation)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string[] baseline = [];
            bool Owns(MemoryCapture reading) => reading.Snapshot.AccountId == identity &&
                (reading.Snapshot.IdentityVerified || LegacyMigration.SameOwner(reading.Snapshot.Xp, known) == true);
            bool Accept(MemoryCapture reading) => Owns(reading) &&
                new[] { "MiscItems", "Suits", "Recipes", "XPInfo" }.Concat(baseline).All(reading.Sections.Contains);
            var capture = await Task.Run(() =>
            {
                baseline = (store.Read<string[]>("memory-sections-" + key) ?? []).Union(migratedSections).ToArray();
                return ReadMemory(pid, identity, cancellation, Accept);
            }, cancellation);
            var complete = Accept(capture);
            lock (sync)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!J.B(settings["memory_scan_enabled"])) return;
                var currentAccount = registry.Accounts.FirstOrDefault(a => a.Key == key);
                if (playingKey != key || Active?.Key != key || GameProcess != pid || gameSession != session || currentAccount?.Id != identity)
                { ScanError = "Game session or account identity changed during capture; reading discarded."; return; }
                if (!Owns(capture))
                {
                    ScanError = "Capture ownership is unverified. Refresh the public profile and try again.";
                    return;
                }
                // The same game sync cannot replace the confirmed inventory.
                var repeatedSync = complete && capture.Snapshot.SourceTimestamp is { } sourceTime &&
                    snapshots.CurrentMetadata(identity)?.SourceTimestamp == sourceTime;
                if (!repeatedSync)
                {
                    var result = snapshots.Commit(capture.Snapshot with { Complete = complete, IdentityVerified = true }, cancellation);
                    Event(result.Reason, result.Promoted ? "info" : "error");
                    if (!result.Promoted)
                    {
                        ScanError = complete ? result.Reason : "Partial capture retained; confirmed inventory unchanged.";
                        return;
                    }
                    store.Write("memory-sections-" + key, baseline.Union(capture.Sections).ToArray());
                }
                // Keep signals received during the read until its result is known.
                // A failed or stale capture must leave them eligible for another attempt.
                if (missionRead != null)
                {
                    // The confirmed snapshot may predate the mission. Allow one explicit
                    // inventory signal in this burst to recheck an unchanged first read.
                    if (repeatedSync && missionRead.Trigger == MemoryTrigger.Inventory &&
                        confirmedMissionRead?.Covers(session, missionRead.Started, MemoryTrigger.Mission) != true)
                        missionRead = missionRead with { Trigger = MemoryTrigger.Mission };
                    confirmedMissionRead = missionRead;
                    if (pendingMemory is { } queued && missionRead.Covers(queued.Session, queued.LastSignal, queued.Trigger))
                        pendingMemory = null;
                }
                lastSuccessfulScan = J.Node(new { t = J.Now, stacks = capture.Snapshot.Counts.Count, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) });
                ScanError = null;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InventoryException or UnauthorizedAccessException)
        {
            lock (sync)
            {
                if (!cancellation.IsCancellationRequested)
                    ScanError = e is InventoryException ? e.Message : "Game memory was not readable; confirmed inventory retained.";
            }
        }
        finally { lock (sync) { ScanRunning = false; memoryCancellation?.Dispose(); memoryCancellation = null; } }
    }
}
