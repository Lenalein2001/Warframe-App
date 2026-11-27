using System.Text;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;
using WarframeCompanion.Services;

internal static class FileNotificationTests
{
    private const string Account = "0123456789abcdef01234567";
    private static readonly TimeSpan NotificationDeadline = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(30);

    internal static void Register(Action<string, Action> test)
    {
        test("File notifications wake the log reader and coalesce a mission burst", () =>
        {
            using var fixture = new Fixture(memoryEnabled: true);
            long clock = 1000;
            var reads = 0;
            var syncs = 0;
            fixture.Runtime.MemoryClock = () => Interlocked.Read(ref clock);
            fixture.Runtime.ReadMemory = (_, id, _, _) =>
            {
                Interlocked.Increment(ref reads);
                return new(InventoryParser.Parse(Encoding.UTF8.GetBytes(Inventory(100)), id),
                    ["MiscItems", "Suits", "Recipes", "XPInfo"]);
            };
            fixture.Runtime.StartGameWatcher();
            fixture.Runtime.Log.Signal += signal =>
            {
                if (signal.Kind == "sync") Interlocked.Increment(ref syncs);
            };
            File.AppendAllText(fixture.LogPath, "MissionSucceeded\nExitLevel\nInventory updated\n");
            WaitUntil(() => Volatile.Read(ref syncs) == 3, "Log changes did not wake the reader");

            Interlocked.Exchange(ref clock, 4000);
            File.AppendAllText(fixture.LogPath, "Inventory updated\n");
            WaitUntil(() => fixture.Snapshots.CurrentMetadata(Account)?.SourceTimestamp == 100, "The mission sync was not saved");

            File.AppendAllText(fixture.LogPath, "Inventory updated\nExitLevel\n");
            WaitUntil(() => Volatile.Read(ref syncs) == 6, "Late mission markers were not consumed");
            Interlocked.Exchange(ref clock, 21000);
            fixture.AppendMarkerAndWait(12);
            fixture.AppendMarkerAndWait(13);
            Check(Volatile.Read(ref reads) == 1 && fixture.Snapshots.History(Account).Count == 1,
                "Duplicate wakes or markers caused another read or observation");
        });

        test("File notifications import an atomic inventory replacement with matching length and timestamp", () =>
        {
            using var fixture = new Fixture();
            var stamp = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
            File.WriteAllText(fixture.InventoryPath, Inventory(100));
            File.SetLastWriteTimeUtc(fixture.InventoryPath, stamp);
            fixture.Runtime.StartGameWatcher();
            WaitUntil(() => fixture.Snapshots.CurrentMetadata(Account)?.SourceTimestamp == 100, "Initial watched inventory was not imported");

            void Replace(string contents, DateTime timestamp)
            {
                var replacement = fixture.InventoryPath + ".next";
                File.WriteAllText(replacement, contents);
                File.SetLastWriteTimeUtc(replacement, timestamp);
                File.Move(replacement, fixture.InventoryPath, overwrite: true);
            }

            Replace(Inventory(200), stamp);
            WaitUntil(() => fixture.Snapshots.CurrentMetadata(Account)?.SourceTimestamp == 200, "Atomic replacement was missed");
            Replace(Inventory(200), stamp.AddSeconds(10));
            File.SetLastWriteTimeUtc(fixture.InventoryPath, stamp.AddSeconds(20));
            fixture.AppendMarkerAndWait(12);
            fixture.AppendMarkerAndWait(13);
            Check(fixture.Snapshots.History(Account).Count == 2, "Metadata changes or duplicate notifications added duplicate observations");
        });

        test("File notifications recover an inventory export whose watch directory appeared after startup", () =>
        {
            using var fixture = new Fixture(inventoryInNewDirectory: true, poll: TimeSpan.FromMilliseconds(250));
            fixture.Runtime.StartGameWatcher();
            Check(!Directory.Exists(Path.GetDirectoryName(fixture.InventoryPath)), "The watch directory already exists");
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.InventoryPath)!);
            File.WriteAllText(fixture.InventoryPath, Inventory(100));
            WaitUntil(() => fixture.Snapshots.CurrentMetadata(Account)?.SourceTimestamp == 100, "Reconciliation missed an export created without an active file watch");

            fixture.Runtime.GamePollInterval = LongPoll;
            File.WriteAllText(fixture.InventoryPath, Inventory(200));
            WaitUntil(() => fixture.Snapshots.CurrentMetadata(Account)?.SourceTimestamp == 200, "A recovered watch missed the next export");
            Check(fixture.Snapshots.History(Account).Count == 2, "Watch recovery duplicated import history");
        });

        test("File notifications recover a lost in-place write with unchanged metadata", () =>
        {
            using var fixture = new Fixture(poll: TimeSpan.FromMilliseconds(100));
            fixture.Runtime.FileNotificationsEnabled = false;
            fixture.Runtime.InventoryVerificationInterval = TimeSpan.FromMilliseconds(250);
            File.WriteAllText(fixture.InventoryPath, Inventory(100));
            var original = File.GetLastWriteTimeUtc(fixture.InventoryPath);
            fixture.Runtime.StartGameWatcher();
            WaitUntil(() => fixture.Snapshots.CurrentMetadata(Account)?.SourceTimestamp == 100, "Initial import missing");
            File.WriteAllText(fixture.InventoryPath, Inventory(200));
            File.SetLastWriteTimeUtc(fixture.InventoryPath, original);
            WaitUntil(() => fixture.Snapshots.CurrentMetadata(Account)?.SourceTimestamp == 200, "Periodic verification missed changed bytes");
            Check(fixture.Snapshots.History(Account).Count == 2, "Reconciliation saved redundant observations");
        });

        test("File notifications keep the timed fallback and cancellation without a watch", () =>
        {
            using var wake = new FileChangeWake();
            wake.Watch(null, null);
            var wait = wake.Wait(TimeSpan.FromMilliseconds(150), CancellationToken.None);
            Check(!wait.IsCompleted, "Missing watches caused a busy loop");
            Check(wait.Wait(NotificationDeadline), "The timer did not wake the reader without a notification");
            using var cancellation = new CancellationTokenSource();
            var cancelled = wake.Wait(LongPoll, cancellation.Token);
            cancellation.Cancel();
            try
            {
                cancelled.WaitAsync(NotificationDeadline).GetAwaiter().GetResult();
                throw new Exception("A canceled wait completed normally");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
    }

    private static string Inventory(int sync) =>
        $$"""{"AccountId":"{{Account}}","MiscItems":[],"Suits":[],"Recipes":[],"XPInfo":[],"LastInventorySync":{{sync}}}""";

    private static void WaitUntil(Func<bool> ready, string message) =>
        Check(SpinWait.SpinUntil(ready, NotificationDeadline), message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("wfc-file-notifications-").FullName;
        private readonly StateStore state;
        internal CompanionRuntime Runtime { get; }
        internal SnapshotStore Snapshots { get; }
        internal string LogPath { get; }
        internal string InventoryPath { get; }

        internal Fixture(bool memoryEnabled = false, bool inventoryInNewDirectory = false, TimeSpan? poll = null)
        {
            LogPath = Path.Combine(directory, "EE.log");
            InventoryPath = inventoryInNewDirectory ? Path.Combine(directory, "exports", "inventory.json") : Path.Combine(directory, "inventory.json");
            File.WriteAllText(LogPath, $"Logged in Fixture ({Account})\n");
            state = new(Path.Combine(directory, "state.sqlite"));
            state.Write("registry", new RegistryState { Active = "fixture", Accounts = [new() { Key = "fixture", Name = "Fixture", Id = Account }] });
            var settings = Settings.Defaults();
            settings["ee_log_path"] = LogPath;
            settings["inventory_json_path"] = InventoryPath;
            settings["memory_scan_enabled"] = memoryEnabled;
            settings["ocr_enabled"] = false;
            state.Write("settings", settings);
            Snapshots = new(Path.Combine(directory, "companion.sqlite"));
            Runtime = new(directory, offline: true)
            {
                FindGameProcess = () => 123,
                ReadMemory = (_, _, _, _) => throw new InvalidOperationException("Unexpected fixture read"),
                GamePollInterval = poll ?? LongPoll
            };
        }

        internal void AppendMarkerAndWait(int rank)
        {
            File.AppendAllText(LogPath, $"OnMasteryIconReady1(Loaded:/Rank{rank}_icon)\n");
            WaitUntil(() => state.Read<RegistryState>("registry")?.Accounts.Single().Badge == rank, "The reader did not process the marker after the burst");
        }

        public void Dispose()
        {
            Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            // Resolve and verify the exact directory created by this fixture before recursive cleanup.
            var root = Path.GetFullPath(Path.GetTempPath());
            var target = Path.GetFullPath(directory);
            var name = Path.GetRelativePath(root, target);
            if (!name.StartsWith("wfc-file-notifications-", StringComparison.Ordinal) || name != Path.GetFileName(name))
                throw new InvalidOperationException("Fixture directory escaped its temporary root");
            Directory.Delete(target, recursive: true);
        }
    }
}
