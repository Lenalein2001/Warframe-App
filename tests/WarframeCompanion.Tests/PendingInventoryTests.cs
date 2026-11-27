using System.Text.Json;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;
using WarframeCompanion.Services;

internal static class PendingInventoryTests
{
    internal static void Register(Action<string, Action> test)
    {
        test("Pending inventory picks correct and undo exactly one reward per screen", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, reward: 3, alternate: 2);
            var first = fixture.Screen(fixture.At - 50);
            Check(first == fixture.Screen(fixture.At - 50), "OCR refresh changed the reward screen identity");
            fixture.Pick(first, "/Reward");
            fixture.Pick(first, "/Reward");
            Check(fixture.View.HeldStack("/Reward") == 4, "Replayed pick incremented the count again");
            Check(fixture.Raw!.Counts["/Reward"] == 3, "A pending pick rewrote the confirmed inventory");
            fixture.Screen(fixture.At - 50);
            Check(J.S(fixture.History.Single(), "picked") == "/Reward", "Refreshing the same screen erased the selected reward");
            fixture.Pick(first, "/Alt");
            Check(fixture.View.HeldStack("/Reward") == 3 && fixture.View.HeldStack("/Alt") == 3, "Correction left both picks applied");
            fixture.Pick(first, null);
            Check(fixture.View.HeldStack("/Alt") == 2 && J.S(fixture.History.Single(), "picked") == null, "Undo left a pending reward");
            Reject(() => fixture.Pick(first, "/Unlisted"), "Accepted an item absent from that reward screen");
            var second = fixture.Screen(fixture.At - 49, screenId: "fixture-next-screen");
            Check(first != second, "Distinct screens with the same drops were combined");
            fixture.Pick(first, "/Reward");
            fixture.Pick(second, "/Reward");
            Check(fixture.View.HeldStack("/Reward") == 5, "Two distinct screens selecting the same item should add two copies");
        });

        test("Pending inventory picks survive restart and delayed sync while staying with the original account", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, reward: 3);
            var screen = fixture.Screen(fixture.At - 50);
            fixture.Pick(screen, "/Reward");
            fixture.Switch("second");
            fixture.Import(fixture.At - 10, reward: 10);
            Reject(() => fixture.Pick(screen, "/Reward", "first"), "An action from the old page was accepted after switching accounts");
            Reject(() => fixture.Pick(screen, "/Reward", "second"), "A different account selected another account's reward");
            Check(fixture.View.HeldStack("/Reward") == 10, "The first account's reward leaked into the second account");
            fixture.Restart();
            Check(fixture.View.HeldStack("/Reward") == 10, "Restart moved the pending reward onto the selected account");
            fixture.Switch("first");
            Check(fixture.View.HeldStack("/Reward") == 4 && J.S(fixture.History.Single(), "picked") == "/Reward", "Restart lost the pending pick");
            fixture.Import(fixture.At - 60, reward: 3);
            fixture.Import(fixture.At - 30, reward: 0, complete: false);
            fixture.Import(fixture.At - 70, reward: 0);
            Check(fixture.View.HeldStack("/Reward") == 4, "A delayed, partial or stale read prematurely settled a pick");
            fixture.Import(fixture.At - 20, reward: 4);
            Check(fixture.View.HeldStack("/Reward") == 4 && fixture.Raw!.Counts["/Reward"] == 4, "The next confirmed inventory double-counted the picked reward");
            fixture.Restart();
            fixture.Pick(screen, "/Reward");
            Check(fixture.View.HeldStack("/Reward") == 4, "Restart or repeated action applied an already reconciled pick");
            fixture.Pick(screen, null);
            Check(fixture.View.HeldStack("/Reward") == 4, "Undo changed a historical confirmed quantity");
            var unmarked = fixture.Screen(fixture.At - 79);
            fixture.Pick(unmarked, "/Reward");
            Check(fixture.View.HeldStack("/Reward") == 4, "Retrospective choice of a reward already covered by the sync was added again");
        });

        test("Pending inventory foundry claims are replay-safe through account switches and restart", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, material: 4, inFoundry: true);
            var build = fixture.Build;
            Check(J.N(build, "baseline") > 0 && J.S(build, "token") != null, "Item detail did not provide a guarded build action");
            fixture.Built(build);
            fixture.Built(build);
            Check(J.B(fixture.Build["pending"]), "The item did not reflect its pending build");
            Check(fixture.View.Owned("/Gun") == 1 && fixture.View.Inventory!.Foundry.Length == 0, "A repeated claim was counted twice or left the old job visible");
            Check(fixture.View.HeldStack("/Metal") == 4, "Claiming a finished foundry job consumed the ingredients again");
            Check(fixture.Raw!.Equipment.Count == 0 && fixture.Raw.Foundry.Length == 1, "Claiming a job rewrote confirmed inventory");
            fixture.Switch("second");
            fixture.Import(fixture.At - 40, material: 9, guns: 2);
            Reject(() => fixture.Built(build), "Stale build action targeted the wrong account");
            Check(fixture.View.Owned("/Gun") == 2 && fixture.View.HeldStack("/Metal") == 9, "First account's claim leaked into second");
            fixture.Restart();
            fixture.Switch("first");
            Check(fixture.View.Owned("/Gun") == 1 && fixture.View.Inventory!.Foundry.Length == 0, "Restart failed to restore the pending claim");
            fixture.Import(fixture.At - 10, material: 4, inFoundry: true);
            Check(fixture.View.Owned("/Gun") == 1 && fixture.View.Inventory!.Foundry.Length == 0, "A newer capture made before the claim cleared it");
            fixture.Import(fixture.At + 30, material: 4, guns: 1);
            Check(fixture.View.Owned("/Gun") == 1 && fixture.View.Inventory!.Foundry.Length == 0, "Reconciliation double-counted the foundry output");
            fixture.Restart();
            try { fixture.Built(build); } catch (InventoryException) { }
            Check(fixture.View.Owned("/Gun") == 1 && !J.B(fixture.BuildAction?["pending"]), "Replaying a reconciled claim created a second build");
        });

        test("Pending inventory unlisted builds reserve ingredients once until the confirmed sync", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, material: 4);
            var build = fixture.Build;
            fixture.Built(build);
            fixture.Built(build);
            Check(fixture.View.Owned("/Gun") == 1 && fixture.View.HeldStack("/Metal") == 2, "Marking a build failed to project its output and ingredients once");
            Check(fixture.Raw!.Counts["/Metal"] == 4, "Projected consumption modified confirmed quantities");
            Check(fixture.View.HeldStack("/Blueprint") == 0 && fixture.Raw.Counts["/Blueprint"] == 1, "Single-use blueprint was not projected once");
            fixture.Import(fixture.At + 30, material: 2, guns: 1);
            Check(fixture.View.Owned("/Gun") == 1 && fixture.View.HeldStack("/Metal") == 2, "Reconciliation consumed ingredients twice");
        });

        test("Pending inventory trusts a dated confirmed sync even when the item count stays the same", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, reward: 3);
            var screen = fixture.Screen(fixture.At - 50);
            fixture.Pick(screen, "/Reward");
            fixture.Import(null, reward: 3);
            fixture.Import(fixture.At - 100, reward: 3);
            Check(fixture.View.HeldStack("/Reward") == 4, "An undated or equal sync settled a pending pick");
            fixture.Import(fixture.At - 50, reward: 3);
            Check(fixture.View.HeldStack("/Reward") == 3, "The authoritative new count retained an unconfirmed addition");
            Check(J.S(fixture.History.Single(), "picked") == "/Reward", "Settling the pending inventory changed the recorded selection");
        });

        test("Pending inventory rejects an unused build action from an older baseline", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, material: 4, inFoundry: true);
            var stale = fixture.Build;
            fixture.Import(fixture.At - 40, material: 4, guns: 1);
            Reject(() => fixture.Built(stale), "A stale build action was accepted against a different confirmed inventory");
            Check(fixture.View.Owned("/Gun") == 1, "Stale build action duplicated the confirmed item");
        });

        test("Pending inventory follows provisional account linking and settles under the explicit ID", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, reward: 3);
            var screen = fixture.Screen(fixture.At - 50);
            fixture.Pick(screen, "/Reward");
            fixture.Link("0123456789abcdef01234567");
            fixture.Restart();
            Check(fixture.View.HeldStack("/Reward") == 4 && J.S(fixture.History.Single(), "picked") == "/Reward", "Linking lost the account's pending reward");
            fixture.Import(fixture.At - 20, reward: 4);
            fixture.Pick(screen, "/Reward");
            Check(fixture.View.HeldStack("/Reward") == 4, "Linked inventory sync did not settle the pending reward");
            fixture.Switch("second");
            fixture.Import(fixture.At, reward: 6);
            Check(fixture.View.HeldStack("/Reward") == 6, "Linking exposed a pick to an unrelated account");
        });

        test("Pending inventory pick and covering sync can arrive concurrently without double-counting", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, reward: 3);
            var screen = fixture.Screen(fixture.At - 50);
            Parallel.Invoke(
                () => fixture.Pick(screen, "/Reward"),
                () => fixture.Import(fixture.At - 20, reward: 4));
            Check(fixture.Raw!.Counts["/Reward"] == 4 && fixture.View.HeldStack("/Reward") == 4, "Concurrent pick and sync duplicated a reward");
            Check(J.S(fixture.History.Single(), "picked") == "/Reward", "Sync lost the concurrent reward annotation");
            fixture.Restart();
            Check(fixture.View.HeldStack("/Reward") == 4, "A settled concurrent pick was replayed during startup");
        });

        test("Pending inventory keeps a pre-baseline pick until the first covering sync", () =>
        {
            using var fixture = new Fixture();
            var screen = fixture.Screen(fixture.At - 50);
            fixture.Pick(screen, "/Reward");
            Check(J.S(fixture.History.Single(), "picked") == "/Reward" && fixture.Raw == null, "The pick required or invented a confirmed inventory");
            fixture.Restart();
            fixture.Import(fixture.At - 60, reward: 3);
            Check(fixture.View.HeldStack("/Reward") == 4, "The first inventory was older than the pending pick");
            fixture.Import(fixture.At - 20, reward: 4);
            Check(fixture.View.HeldStack("/Reward") == 4, "The first covering sync counted the reward twice");
        });

        test("Pending inventory offers a distinct claim for each identical foundry job", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, material: 4, inFoundry: true, jobs: 2);
            var first = fixture.Build;
            fixture.Built(first);
            Check(fixture.View.Inventory!.Foundry.Length == 1 && fixture.View.Owned("/Gun") == 1, "One claim consumed both matching jobs");
            fixture.Restart();
            var second = fixture.Build;
            Check(J.S(first, "token") != J.S(second, "token"), "A second foundry job reused the first claim's token");
            fixture.Built(first);
            Check(fixture.View.Inventory!.Foundry.Length == 1, "Replaying the first claim removed the second job");
            fixture.Built(second);
            Check(fixture.View.Inventory!.Foundry.Length == 0 && fixture.View.Owned("/Gun") == 2, "The second distinct claim was missed");
            fixture.Import(fixture.At + 30, material: 4, guns: 2);
            Check(fixture.View.Owned("/Gun") == 2, "Reconciliation counted two matching jobs twice");
        });

        test("Pending inventory distinguishes later foundry jobs with no completion timestamp", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, inFoundry: true, unknownCompletion: true);
            var first = fixture.Build;
            fixture.Built(first);
            var syncedAt = J.Now;
            fixture.Import(syncedAt, guns: 1, inFoundry: true, unknownCompletion: true);
            fixture.Restart();
            var second = fixture.Build;
            Check(J.S(first, "token") != J.S(second, "token"), "A later undated job collided with a settled claim");
            Check(SpinWait.SpinUntil(() => J.Now > syncedAt, TimeSpan.FromSeconds(1)), "The clock did not advance after the fixture sync");
            fixture.Built(second);
            Check(fixture.View.Owned("/Gun") == 2 && fixture.View.Inventory!.Foundry.Length == 0, "The next undated job was ignored");
        });
        test("Pending inventory actions from one rendered page survive out-of-order delivery", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, material: 6, inFoundry: true, jobs: 2);
            var marks = J.A(fixture.Get("foundry"), "pending").Select(j => j["build"]!).ToArray();
            Check(marks.Length == 2 && J.S(marks[0], "token") != J.S(marks[1], "token"), "Two displayed jobs shared an action");
            fixture.Built(marks[1]); fixture.Restart(); fixture.Built(marks[0]); fixture.Built(marks[1]);
            Check(fixture.View.Owned("/Gun") == 2 && fixture.View.Inventory!.Foundry.Length == 0, "A distinct claim was lost or replayed");
            var screen = fixture.Screen(fixture.At - 50);
            void Pick(string uid, string? previous) => fixture.Send("rewards/pick", J.Node(new { accountKey = "first", id = screen, uid, previous }));
            Pick("/Reward", null); Pick("/Alt", "/Reward");
            Reject(() => Pick("/Reward", null), "A delayed retry overwrote a corrected pick");
            Check(fixture.View.HeldStack("/Alt") == 1 && fixture.View.HeldStack("/Reward") == 0, "Correction projected both rewards");
        });
        test("Pending inventory can record a second unlisted build without replaying the first", () =>
        {
            using var fixture = new Fixture();
            fixture.Import(fixture.At - 100, material: 6);
            var first = fixture.Build;
            fixture.Built(first); fixture.Restart();
            var next = fixture.Get("item", "/Gun")["anotherBuild"]!;
            fixture.Built(next); fixture.Built(first); fixture.Built(next);
            Check(fixture.View.Owned("/Gun") == 2 && fixture.View.HeldStack("/Metal") == 2, "Second manual build or its cost was miscounted");
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InventoryException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("wfc-inventory-actions-").FullName;
        private CompanionRuntime runtime;
        private readonly SnapshotStore snapshots;
        private string account = "first";
        internal long At { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        internal GameView View => runtime.View();
        internal JsonNode Get(string route, string key = "") => runtime.Get(route, new Dictionary<string,string> { ["key"] = key });
        internal void Send(string route, JsonNode body) => Post(route, body);
        internal InventorySnapshot? Raw => snapshots.Current(account);
        internal JsonNode[] History => J.A(runtime.Get("history", new Dictionary<string, string>()), "entries").ToArray();
        internal JsonNode? BuildAction => runtime.Get("item", new Dictionary<string, string> { ["key"] = "/Gun" })["build"];
        internal JsonNode Build => BuildAction ?? throw new InvalidOperationException("Item detail is missing its build action");

        internal Fixture()
        {
            var state = new StateStore(Path.Combine(directory, "state.sqlite"));
            state.Write("registry", new RegistryState { Active = account, Accounts = [new() { Key = "first", Name = "First" }, new() { Key = "second", Name = "Second" }] });
            snapshots = new(Path.Combine(directory, "companion.sqlite"));
            runtime = Open();
        }

        private CompanionRuntime Open()
        {
            var result = new CompanionRuntime(directory, offline: true);
            result.Catalog.Items["/Gun"] = new()
            {
                Uid = "/Gun", Name = "Fixture Gun", Category = "Primary", Kind = "Primary",
                Components = [new("/Blueprint", 1, "part", "Fixture Gun Blueprint"), new("/Metal", 2, "resource", "Metal")]
            };
            result.Catalog.Parts["/Blueprint"] = new("/Blueprint", "Fixture Gun Blueprint", null, null);
            result.Catalog.Recipes["/Blueprint"] = new("/Gun", 1, null, null, [new("/Metal", "Metal", 2)], ConsumeOnUse: true);
            result.Catalog.Resources["/Metal"] = "Metal";
            return result;
        }

        internal string Screen(long time, string? screenId = null)
        {
            runtime.History.Record(J.Node(new { t = time, screenId, items = new[] { new { name = "Reward", uid = "/Reward" }, new { name = "Alternate", uid = "/Alt" } } }), account);
            return J.S(History.SingleOrDefault(row => J.D(row["t"]) == time), "id")
                ?? throw new InvalidOperationException("A recorded reward screen is missing its identifier");
        }

        internal void Pick(string screen, string? uid, string? accountKey = null) =>
            Post("rewards/pick", J.Node(new { id = screen, accountKey = accountKey ?? account, uid }));

        internal void Built(JsonNode build) => Post("foundry/built", build.DeepClone());

        internal void Import(double? time, int reward = 0, int alternate = 0, int material = 0, int guns = 0, bool inFoundry = false,
            bool complete = true, int jobs = 1, bool unknownCompletion = false)
        {
            var body = (JsonObject)JsonSerializer.SerializeToNode(new
            {
                LastInventorySync = time,
                MiscItems = new[] { new { ItemType = "/Reward", ItemCount = reward }, new { ItemType = "/Alt", ItemCount = alternate }, new { ItemType = "/Metal", ItemCount = material } },
                Recipes = new[] { new { ItemType = "/Blueprint", ItemCount = inFoundry ? 0 : 1 } },
                Suits = Array.Empty<object>(),
                XPInfo = Array.Empty<object>(),
                LongGuns = Enumerable.Repeat(new { ItemType = "/Gun" }, guns),
                PendingRecipes = Enumerable.Repeat(new { ItemType = "/Blueprint", CompletionDate = unknownCompletion ? (long?)null : At - 120 }, inFoundry ? jobs : 0)
            })!;
            if (!time.HasValue) body.Remove("LastInventorySync");
            if (!complete) body.Remove("XPInfo");
            Post("inventory/import", body);
        }

        internal void Switch(string next)
        {
            Post("account/switch", J.Node(new { key = next }));
            account = next;
        }

        internal void Link(string id) => Post("account/set", J.Node(new { key = account, id }));

        internal void Restart()
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            runtime = Open();
        }

        private void Post(string route, JsonNode payload) => runtime.Post(route, payload, CancellationToken.None).GetAwaiter().GetResult();

        public void Dispose()
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var target = Path.GetFullPath(directory);
            var name = Path.GetRelativePath(Path.GetFullPath(Path.GetTempPath()), target);
            if (!name.StartsWith("wfc-inventory-actions-", StringComparison.Ordinal) || name != Path.GetFileName(name))
                throw new InvalidOperationException("Fixture escaped its temporary directory");
            Directory.Delete(target, recursive: true);
        }
    }
}
