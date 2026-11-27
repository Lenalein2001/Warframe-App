using System.Text.Json.Nodes;
using WarframeCompanion.Services;

internal static class DuviriTests
{
    internal static void Register(Action<string, Action> test)
    {
        test("Duviri Cave rolls survive cached messages, replay and account switches", () =>
        {
            const string first = "0123456789abcdef01234567", second = "1123456789abcdef01234567";
            const string start = "RollDuviriOffers - Generating new Duviri offers", done = "RollDuviriOffers - Duviri Seed: 4";
            const string cached = "RollDuviriOffers - Using cached duviri offers";
            var dir = Directory.CreateTempSubdirectory("wfc-duviri-").FullName;
            var log = Path.Combine(dir, "EE.log");
            void LoginLog(params string[] lines) => File.WriteAllLines(log, lines);
            try
            {
                var store = new StateStore(Path.Combine(dir, "state.sqlite"));
                var settings = Settings.Defaults();
                settings["ee_log_path"] = log;
                settings["inventory_json_path"] = null;
                settings["memory_scan_enabled"] = false;
                store.Write("settings", settings);
                var account = new AccountState { Key = "first", Id = first, Name = "First" };
                account.Manual.Tasks.Add(new("/Lotus/A", null, 1));
                store.Write("registry", new RegistryState { Active = "first", Accounts = [account, new() { Key = "second", Id = second, Name = "Second" }] });
                void Inspect(Action<CompanionRuntime> verify)
                {
                    var runtime = new CompanionRuntime(dir, true) { FindGameProcess = () => null, GamePollInterval = TimeSpan.FromMinutes(5) };
                    try
                    {
                        runtime.StartGameWatcher();
                        runtime.Post("account/switch", J.Node(new { key = "first" }), default).GetAwaiter().GetResult();
                        verify(runtime);
                    }
                    finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                }
                const string header = "0.0 [UTC: Wed Nov 25 12:00:00 2026]";
                LoginLog(header, $"1 Logged in First ({first})", "2 " + start,
                    "3 /Lotus/A generated as an owned Cave selection for category: 3",
                    "3 /Lotus/A generated as an owned Cave selection for category: 3", "4 " + done, "5 " + cached, "5 " + done,
                    "6 " + start, "7 /Lotus/Partial generated as an owned Cave selection for category: 1",
                    $"8 Logged in Second ({second})", "9 " + done);
                void Verify(CompanionRuntime runtime)
                {
                    var world = runtime.Get("world", new Dictionary<string,string>());
                    var roll = world["duviri"]!;
                    var pick = J.A(J.A(roll, "groups").Single(), "picks").Single();
                    if (J.S(pick, "uid") != "/Lotus/A" || !J.B(pick["onBoard"])) throw new Exception("Wrong saved Cave offer or farming goal");
                    var task = J.A(runtime.Get("tasks", new Dictionary<string,string>()), "tasks").Single();
                    if (J.S(task, "cave") != "unknown") throw new Exception("Offline board presented an unverified roll as current");
                    if (WorldService.Epoch(J.S(world["resets"], "weekly")) == null) throw new Exception("Offline Circuit reset missing");
                    runtime.Post("account/switch", J.Node(new { key = "second" }), default).GetAwaiter().GetResult();
                    if (runtime.Get("world", new Dictionary<string,string>())["duviri"] != null) throw new Exception("Cross-account partial roll accepted");
                }
                Inspect(Verify);
                LoginLog("0.0 [UTC: Tue Nov 24 12:00:00 2026]", $"1 Logged in First ({first})", "2 " + start,
                    "3 /Lotus/Old generated as an unowned Cave selection for category: 0", "4 " + done);
                Inspect(Verify);
            }
            finally { Directory.Delete(dir, true); }
        });

        test("Duviri Cave freshness and weekly Circuit reset at the Monday UTC boundary", () =>
        {
            if (WorldService.CaveFreshness(110, 100, 200, 150) != "current" ||
                WorldService.CaveFreshness(90, 100, 200, 150) != "stale" ||
                WorldService.CaveFreshness(110, 100, 200, 200) != "stale" ||
                WorldService.CaveFreshness(null, 100, 200, 150) != "unknown") throw new Exception("Invalid freshness");
            var sunday = new DateTimeOffset(2026, 11, 29, 23, 59, 59, TimeSpan.Zero);
            var monday = sunday.AddSeconds(1);
            if (WorldService.NextWeekly(sunday) != monday || WorldService.NextWeekly(monday) != monday.AddDays(7))
                throw new Exception("Wrong weekly Circuit rollover");
        });
    }
}
