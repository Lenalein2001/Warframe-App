using System.Text;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;
using WarframeCompanion.Services;

internal static class MissionRefreshTests
{
    private const string Account = "0123456789abcdef01234567";
    private const string SecondAccount = "1123456789abcdef01234567";

    internal static void Register(Action<string, Action> test)
    {
        test("Session boundary: a new process cannot inherit a login from an offline log", () =>
        {
            using var fixture = new Fixture(initialPid: 0);
            var attempts = 0;
            fixture.Runtime.ReadMemory = (_, id, _, _) => { Interlocked.Increment(ref attempts); return Capture(200, id); };
            fixture.SetPid(456);
            fixture.Append(2000, "Inventory updated");
            if (J.B(fixture.Runtime.Summary()["account"]?["playing"]))
                throw new Exception("The new process inherited the offline account");
            fixture.Append(12000, "AccountId: " + Account);
            fixture.Append(14000, $"Logged in Second ({SecondAccount})", "Inventory updated");
            fixture.Tick(17000);
            Wait(() => fixture.Store.CurrentMetadata(SecondAccount)?.SourceTimestamp == 200, "New login did not refresh");
            if (attempts != 1 || fixture.Store.History(Account).Count != 0)
                throw new Exception("An orphan sync refreshed the previous account");
        });

        test("Session boundary: late detection of the same process retains its pending mission", () =>
        {
            using var fixture = new Fixture(initialPid: 0);
            fixture.Runtime.GameStartedAt = _ => DateTimeOffset.UtcNow.AddMinutes(-1);
            fixture.Runtime.ReadMemory = (_, id, _, _) => Capture(250, id);
            fixture.Append(1000, "Inventory updated", "ExitLevel");
            fixture.SetPid(456);
            fixture.Tick(4000);
            Wait(() => fixture.Store.CurrentMetadata(Account)?.SourceTimestamp == 250, "Late process discovery lost a verified session's queued mission");
            if (fixture.Store.History(Account).Count != 1) throw new Exception("Duplicate snapshots after late process discovery");
        });

        test("Session boundary: replacement game log catches up once for its final login", () =>
        {
            using var fixture = new Fixture();
            var attempts = 0;
            fixture.Runtime.ReadMemory = (_, id, _, _) => { Interlocked.Increment(ref attempts); return Capture(200, id); };
            fixture.RestartWithLog($"Logged in Fixture ({Account})\nMissionSucceeded\nLogged in Second ({SecondAccount})\nInventory updated\nExitLevel\n", expectedSignals: 5);
            Wait(() => fixture.Runtime.Summary()["account"]?["id"]?.ToString() == SecondAccount, "Restart did not follow the final login");
            fixture.Tick(12000);
            Wait(() => fixture.Store.CurrentMetadata(SecondAccount)?.SourceTimestamp == 200, "Mission already in the replacement log was missed");
            fixture.Append(14000, "ExitLevel", "Inventory updated");
            fixture.Append(28000, "AccountId: " + SecondAccount);
            fixture.Append(30000, "AccountId: " + SecondAccount);
            if (attempts != 1 || fixture.Store.History(Account).Count != 0)
                throw new Exception("Replay or trailing mission messages caused another read");
        });

        test("Session boundary: a repeated player-name notice preserves the in-flight mission read", () =>
        {
            using var fixture = new Fixture();
            fixture.StartBlockedRead(() => Capture(100), nextSync: 200);
            fixture.Append(5000, "Sys [Info]: Player name changed to Fixture");
            fixture.FinishRead();
            fixture.Tick(16000);
            Wait(() => fixture.Store.CurrentMetadata(Account) != null, "The player-name notice lost the mission read");
            if (fixture.Attempts != 1 || fixture.Store.CurrentMetadata(Account)?.SourceTimestamp != 100)
                throw new Exception("A repeated name notice restarted the active mission read");
        });

        test("Session boundary: a same-file rewrite between polls recovers the new login without replaying overlays", () =>
        {
            using var fixture = new Fixture();
            var overlays = 0;
            fixture.Runtime.RewardScreenOpened += () => Interlocked.Increment(ref overlays);
            fixture.Runtime.ReadMemory = (_, id, _, _) => Capture(200, id);
            fixture.RestartWithLog($"Logged in Second ({SecondAccount})\nOpenVoidProjectionRewardScreen\nInventory updated\n",
                expectedSignals: 2, rewrite: true);
            fixture.Tick(5000);
            Wait(() => fixture.Store.CurrentMetadata(SecondAccount)?.SourceTimestamp == 200, "Rewritten login or mission was lost");
            if (overlays != 0 || fixture.Store.History(Account).Count != 0)
                throw new Exception("Restart replay opened an old overlay or refreshed the old account");
        });

        test("Session boundary: log rotation while the new process is still missing keeps its first mission", () =>
        {
            using var fixture = new Fixture();
            fixture.Runtime.ReadMemory = (_, id, _, _) => Capture(300, id);
            fixture.SetPid(0);
            fixture.Append(1000, "AccountId: " + Account);
            fixture.ReplaceWhileStopped($"Logged in Second ({SecondAccount})\nInventory updated\n");
            fixture.SetPid(456);
            fixture.WaitForSignals(3);
            fixture.Tick(6000);
            Wait(() => fixture.Store.CurrentMetadata(SecondAccount)?.SourceTimestamp == 300, "First mission was consumed before process discovery");
            if (fixture.Store.History(Account).Count != 0) throw new Exception("Previous account received the first mission");
        });

        test("Session boundary: a restart backlog spanning polls only refreshes the final account", () =>
        {
            using var fixture = new Fixture();
            var reads = 0;
            fixture.Runtime.ReadMemory = (_, id, _, _) => { Interlocked.Increment(ref reads); return Capture(500, id); };
            var syncs = 0;
            fixture.Runtime.Log.Signal += signal =>
            {
                if (signal.Kind == "sync" && Interlocked.Increment(ref syncs) == 1) fixture.Tick(12000);
            };
            fixture.RestartWithLog($"Logged in Fixture ({Account})\nInventory updated\n" + new string('x', 8 * 1024 * 1024) +
                $"\nLogged in Second ({SecondAccount})\nInventory updated\n", expectedSignals: 4);
            fixture.Tick(16000);
            Wait(() => fixture.Store.CurrentMetadata(SecondAccount)?.SourceTimestamp == 500, "Final account did not catch up");
            if (reads != 1 || fixture.Store.History(Account).Count != 0)
                throw new Exception("An intermediate login in the backlog started a read");
        });

        test("Session boundary: log-only monitoring still reads account metadata after rotation", () =>
        {
            using var fixture = new Fixture(initialPid: 0);
            fixture.ReplaceWhileStopped($"Logged in Second ({SecondAccount})\nOnMasteryIconReady1(Loaded:/Rank16_icon)\n");
            var state = fixture.Runtime.Summary();
            if (!J.A(state, "accounts").Any(a => J.S(a, "id") == SecondAccount && J.N(a, "masteryRank") == 16))
                throw new Exception("Log-only account tracking stalled waiting for a process");
        });

        test("Session boundary: file identity follows the open log across a path replacement", () =>
        {
            using var fixture = new Fixture();
            var path = Path.Combine(fixture.Runtime.DataDirectory, "rotation-test.log");
            File.WriteAllText(path, "old");
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var original = NativeEngine.FileIdentity(reader.SafeFileHandle);
            File.Move(path, path + ".old");
            File.WriteAllText(path, "new");
            if (NativeEngine.FileIdentity(path) == original || NativeEngine.FileIdentity(reader.SafeFileHandle) != original)
                throw new Exception("File identity and open stream could refer to different generations");
        });

        test("Session boundary: an unfinished login cannot cross a process restart", () =>
        {
            using var fixture = new Fixture();
            fixture.AppendWithPartial(2000, "AccountId: " + Account, "Sys [Info]: Logged in ");
            fixture.SetPid(456);
            fixture.Append(3000, "Ghost\nInventory updated");
            var state = fixture.Runtime.Summary();
            if (J.B(state["account"]?["playing"]) || J.A(state, "accounts").Any(a => J.S(a, "name") == "Ghost"))
                throw new Exception("An unfinished login from the old process was joined to the new log");
        });

        test("Session boundary: a renamed account stays linked after a name-only login", () =>
        {
            using var fixture = new Fixture();
            fixture.Append(1000, $"Logged in Renamed ({Account})", "Sys [Info]: Logged in Renamed", "Inventory updated");
            fixture.Runtime.ReadMemory = (_, id, _, _) => Capture(300, id);
            fixture.Tick(4000);
            var state = fixture.Runtime.Summary();
            if (J.S(state["account"], "name") != "Renamed" || J.S(state["account"], "id") != Account || J.A(state, "accounts").Count() != 1)
                throw new Exception("The renamed login split the account or lost its confirmed ID");
            Wait(() => fixture.Store.CurrentMetadata(Account)?.SourceTimestamp == 300, "Renamed account missed its inventory update");
        });

        test("Session boundary: account switch discards an old capture that returns after cancellation", () =>
        {
            using var fixture = new Fixture();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            fixture.Runtime.ReadMemory = (_, id, _, _) =>
            {
                if (id == Account) { entered.Set(); release.Wait(TimeSpan.FromSeconds(12)); }
                return Capture(400, id);
            };
            try
            {
                fixture.Append(1000, "MissionSucceeded");
                fixture.Tick(7000);
                if (!entered.Wait(TimeSpan.FromSeconds(8))) throw new Exception("Old capture did not start");
                fixture.Append(8000, $"Logged in Second ({SecondAccount})", "Inventory updated", "ExitLevel");
                release.Set();
                fixture.Tick(20000);
                Wait(() => fixture.Store.CurrentMetadata(SecondAccount)?.SourceTimestamp == 400, "New account missed its mission");
                if (fixture.Store.History(Account).Count != 0 || fixture.Store.History(SecondAccount).Count != 1)
                    throw new Exception("Late capture crossed the account boundary");
            }
            finally { release.Set(); }
        });
        test("Mission refresh preserves signals received during a failed read", () =>
        {
            using var fixture = new Fixture();
            fixture.StartBlockedRead(() => throw new IOException("Synthetic read failure"), nextSync: 200);
            fixture.Append(5000, "Inventory updated", "ExitLevel", "Inventory updated");
            fixture.FinishRead();
            Wait(() => fixture.Runtime.Summary()["sources"]?["memscan"]?["state"]?.ToString() == "error", "Read failure was not reported");
            fixture.Tick(13000);
            Wait(() => fixture.Store.CurrentMetadata(Account)?.SourceTimestamp == 200, "The failed read swallowed the queued inventory update");
            if (fixture.Attempts != 2 || fixture.Store.History(Account).Count != 1)
                throw new Exception("A single burst produced duplicate reads or snapshots");
        });

        test("Mission refresh honors a new inventory signal near an unresolved mission's expiry", () =>
        {
            using var fixture = new Fixture(linked: false);
            fixture.Runtime.ReadMemory = (_, _, _, _) => Capture(300);
            fixture.Append(1000, "MissionSucceeded");
            fixture.Append(60000, "Inventory updated", "AccountId: " + Account);
            fixture.Tick(62000);
            Wait(() => fixture.Store.CurrentMetadata(Account)?.SourceTimestamp == 300, "New inventory signal expired on the older mission deadline");
        });

        test("Mission refresh reports a stale capture and accepts the next inventory signal", () =>
        {
            using var fixture = new Fixture();
            fixture.Store.Commit(Capture(200).Snapshot with { Complete = true });
            var attempts = 0;
            fixture.Runtime.ReadMemory = (_, _, _, _) => Capture(Interlocked.Increment(ref attempts) == 1 ? 100 : 300);
            fixture.Append(1000, "Inventory updated");
            fixture.Tick(3000);
            Wait(() => fixture.Store.History(Account).Count == 2, "Stale capture never finished");
            Wait(() => !fixture.Runtime.ScanRunning, "Stale read did not finish");
            var status = fixture.Runtime.Summary()["sources"]?["memscan"];
            if (status?["state"]?.ToString() != "error" || status["last"] != null || fixture.Store.CurrentMetadata(Account)?.SourceTimestamp != 200)
                throw new Exception("An older capture was reported as a successful refresh");
            fixture.Append(5000, "Inventory updated");
            fixture.Tick(13000);
            Wait(() => fixture.Store.CurrentMetadata(Account)?.SourceTimestamp == 300, "A stale read suppressed the next inventory signal");
        });

        test("Mission refresh preserves a later inventory update during a long successful read", () =>
        {
            using var fixture = new Fixture();
            fixture.StartBlockedRead(() => Capture(100), nextSync: 200);
            fixture.Append(5000, "Inventory updated");
            fixture.Append(21000, "Inventory updated");
            fixture.FinishRead();
            fixture.Tick(24000);
            Wait(() => fixture.Store.CurrentMetadata(Account)?.SourceTimestamp == 200, "An earlier duplicate hid a later sync during the same read");
            if (fixture.Attempts != 2 || fixture.Store.History(Account).Count != 2)
                throw new Exception("The two distinct updates did not produce exactly two snapshots");
        });

        test("Mission refresh rechecks an unchanged snapshot when an inventory update arrived during capture", () =>
        {
            using var fixture = new Fixture();
            fixture.Store.Commit(Capture(100).Snapshot with { Complete = true });
            fixture.StartBlockedRead(() => Capture(100), nextSync: 200);
            fixture.Append(5000, "Inventory updated", "Inventory updated");
            fixture.FinishRead();
            fixture.Tick(13000);
            Wait(() => fixture.Store.CurrentMetadata(Account)?.SourceTimestamp == 200, "Reading the previous sync swallowed an overlapping inventory update");
            if (fixture.Attempts != 2 || fixture.Store.History(Account).Count != 2)
                throw new Exception("Duplicate notifications added extra history");
        });

        test("Mission refresh limits rechecks when the inventory stays unchanged", () =>
        {
            using var fixture = new Fixture();
            fixture.Store.Commit(Capture(100).Snapshot with { Complete = true });
            fixture.StartBlockedRead(() => Capture(100), nextSync: 100);
            fixture.FinishRead();
            Wait(() => fixture.Runtime.Summary()["sources"]?["memscan"]?["state"]?.ToString() == "ok", "Initial read did not finish");
            fixture.Append(5000, "Inventory updated");
            fixture.Tick(13000);
            Wait(() => fixture.Attempts == 2, "A new inventory signal did not recheck the unchanged snapshot");
            fixture.Append(16000, "Inventory updated", "Inventory updated", "ExitLevel");
            // Two acknowledged log batches allow a queued third read to run after its cooldown.
            fixture.Append(30000, "AccountId: " + Account);
            fixture.Append(31000, "AccountId: " + Account);
            if (fixture.Attempts != 2 || fixture.Store.History(Account).Count != 1)
                throw new Exception("Repeated unchanged syncs caused extra reads or history");
        });
    }

    private static MemoryCapture Capture(int sync, string id = Account) => new(
        InventoryParser.Parse(Encoding.UTF8.GetBytes($$"""{"MiscItems":[],"Suits":[],"Recipes":[],"XPInfo":[],"LastInventorySync":{{sync}},"AccountId":"{{id}}"}"""), id),
        ["MiscItems", "Suits", "Recipes", "XPInfo"]);

    private static void Wait(Func<bool> condition, string failure)
    {
        if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(8))) throw new Exception(failure);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("wfc-mission-").FullName;
        private readonly string log;
        private long clock = 1000;
        private int signals;
        private readonly ManualResetEventSlim finishRead = new();
        private int attempts;
        internal int Attempts => Volatile.Read(ref attempts);
        internal CompanionRuntime Runtime { get; }
        internal SnapshotStore Store { get; }

        private int pid;
        private int polls;
        private Action? restart;

        internal Fixture(bool linked = true, int initialPid = 123)
        {
            log = Path.Combine(directory, "EE.log");
            File.WriteAllText(log, "Sys [Info]: Logged in Fixture\n");
            var state = new StateStore(Path.Combine(directory, "state.sqlite"));
            state.Write("registry", new RegistryState { Active = "fixture", Accounts = [new() { Key = "fixture", Name = "Fixture", Id = linked ? Account : null }] });
            var settings = Settings.Defaults();
            settings["ee_log_path"] = log;
            settings["memory_scan_enabled"] = true;
            state.Write("settings", settings);
            Store = new(Path.Combine(directory, "companion.sqlite"));
            Runtime = new(directory, offline: true);
            Runtime.GameStartedAt = _ => DateTimeOffset.UtcNow;
            pid = initialPid;
            Runtime.FindGameProcess = () =>
            {
                Interlocked.Exchange(ref restart, null)?.Invoke();
                Interlocked.Increment(ref polls);
                var current = Volatile.Read(ref pid);
                return current == 0 ? null : current;
            };
            Runtime.ReadMemory = (_, _, _, _) => throw new InvalidOperationException("Unexpected fixture read");
            Runtime.MemoryClock = () => Interlocked.Read(ref clock);
            Runtime.StartGameWatcher();
            Runtime.Log.Signal += signal =>
            {
                if (signal.Kind is "sync" or "identity" or "login") Interlocked.Increment(ref signals);
            };
        }

        internal void StartBlockedRead(Func<MemoryCapture> first, int nextSync)
        {
            Runtime.ReadMemory = (_, _, token, _) =>
            {
                if (Interlocked.Increment(ref attempts) != 1) return Capture(nextSync);
                finishRead.Wait(token);
                return first();
            };
            Append(1000, "Inventory updated");
            Tick(3000);
            Wait(() => Attempts == 1, "Initial read never started");
        }

        internal void FinishRead() => finishRead.Set();

        internal void SetPid(int value) => Volatile.Write(ref pid, value);

        internal void WaitForSignals(int expected) =>
            Wait(() => Volatile.Read(ref signals) == expected, "Expected log signals did not arrive");

        internal void ReplaceWhileStopped(string content)
        {
            File.WriteAllText(log + ".next", content);
            File.Move(log + ".next", log, overwrite: true);
            var before = Volatile.Read(ref polls);
            Wait(() => Volatile.Read(ref polls) >= before + 2, "Replacement was not polled while the game was stopped");
        }

        internal void RestartWithLog(string content, int expectedSignals, bool rewrite = false)
        {
            var expected = Volatile.Read(ref signals) + expectedSignals;
            Interlocked.Exchange(ref restart, () =>
            {
                if (rewrite) File.WriteAllText(log, content);
                else
                {
                    File.WriteAllText(log + ".next", content);
                    File.Move(log + ".next", log, overwrite: true);
                }
                SetPid(456);
            });
            WaitForSignals(expected);
        }

        internal void Tick(long time) => Interlocked.Exchange(ref clock, time);

        internal void Append(long time, params string[] lines)
            => AppendBatch(time, string.Join('\n', lines) + "\n", lines.Length);

        internal void AppendWithPartial(long time, string line, string trailing)
            => AppendBatch(time, line + "\n" + trailing, 1);

        private void AppendBatch(long time, string text, int count)
        {
            Tick(time);
            var expected = Volatile.Read(ref signals) + count;
            File.AppendAllText(log, text);
            WaitForSignals(expected);
        }

        public void Dispose()
        {
            finishRead.Set();
            Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            finishRead.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
