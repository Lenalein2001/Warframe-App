using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;
using WarframeCompanion.Host;
using WarframeCompanion.Persistence;
using WarframeCompanion.Services;

public static class IntegrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Register(Action<string, Action> test)
    {
        test("Explicit PATH selects OCR ahead of the system-installation fallback", () => InDirectory(directory =>
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract");
            File.WriteAllText(executable, "fixture; never executed");
            try
            {
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
                Check(ScreenCapture.Which("tesseract") == executable, "System OCR installation overrode the explicit test/tool path");
            }
            finally { Environment.SetEnvironmentVariable("PATH", path); }
        }));
        test("Catalog compaction retains region, standing and riven labels without unused exports", () =>
        {
            var catalog = new Catalog
            {
                Dictionary = JsonNode.Parse("""{"node":"Earth","faction":"Syndicate","rank":"Initiate","damage":"Damage","unused":"Discard"}"""),
                Regions = JsonNode.Parse("""{"SolNode1":{"name":"node"}}"""),
                SyndicateExport = JsonNode.Parse("""{"Faction":{"name":"faction","titles":[{"name":"rank"}]}}"""),
                UpgradeExport = JsonNode.Parse("""{"/Lotus/Upgrades/Mods/Randomized/Rifle":{"upgradeEntries":[{"upgradeValues":[{"locTag":"damage"}]}]},"/RegularMod":{"name":"unused"}}""")
            };
            CatalogLoader.CompactMetadata(catalog);
            Check(J.O(catalog.Dictionary).Count() == 4 && J.S(catalog.Dictionary?["damage"]) == "Damage" && J.S(catalog.Dictionary?["rank"]) == "Initiate", "Required labels were lost");
            Check(J.O(catalog.UpgradeExport).Count() == 1 && catalog.UpgradeExport?["/RegularMod"] == null, "Unused mod definitions were retained");
        });
        test("Automatic screenshots select tools for the active Linux desktop", () =>
        {
            Check(!ScreenCapture.ScreenshotCandidates("auto", false, "", false).Contains("grim"), "X11 selected a Wayland-only capture tool");
            Check(ScreenCapture.ScreenshotCandidates("auto", true, "KDE", false).SequenceEqual(["spectacle"]), "KDE selected a wlroots-only tool");
            Check(ScreenCapture.ScreenshotCandidates("auto", true, "GNOME", false).SequenceEqual(["gnome-screenshot"]), "GNOME selected a wlroots-only tool");
            Check(ScreenCapture.ScreenshotCandidates("auto", true, "", true).SequenceEqual(["grim"]), "wlroots capture changed");
            Check(ScreenCapture.ScreenshotCandidates("scrot", true, "KDE", false).SequenceEqual(["scrot"]), "Explicit user choice was overwritten");
        });
        test("Proton log discovery follows Steam libraries and chooses the active prefix", () => InDirectory(directory =>
        {
            var home = Path.Combine(directory, "home");
            var steam = Path.Combine(home, ".local", "share", "Steam", "steamapps"); Directory.CreateDirectory(steam);
            var library = Path.Combine(directory, "Games on another disk");
            File.WriteAllText(Path.Combine(steam, "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" } }");
            string WriteLog(string prefix)
            {
                var log = Path.Combine(prefix, "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "Warframe", "EE.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!); File.WriteAllText(log, "fixture"); return log;
            }
            var moved = WriteLog(Path.Combine(library, "steamapps", "compatdata", "230410"));
            Check(ProtonPaths.FindLog(home) == moved, "Secondary Steam library was missed");
            File.SetLastWriteTimeUtc(moved, DateTime.UtcNow.AddHours(-2));
            var custom = Path.Combine(directory, "custom-prefix"); var current = WriteLog(custom);
            Check(ProtonPaths.FindLog(home, custom) == current, "Explicit active Proton prefix was missed");
            File.WriteAllText(Path.Combine(steam, "libraryfolders.vdf"), "truncated manifest");
            Check(ProtonPaths.FindLog(home, custom) == current, "Broken library manifest hid the explicit prefix");
        }));
        test("Foundry notification callbacks release account locks and failures do not stop refresh", () => InDirectory(directory =>
        {
            var store = new StateStore(Path.Combine(directory, "state.sqlite"));
            store.Write("registry", new RegistryState { Active = "fixture", Accounts = [new() { Key = "fixture", Name = "Fixture" }] });
            var settings = Settings.Defaults(); settings["notify_foundry"] = true; store.Write("settings", settings);
            new SnapshotStore(Path.Combine(directory, "companion.sqlite")).Commit(InventoryParser.Parse(
                Encoding.UTF8.GetBytes("""{"MiscItems":[],"PendingRecipes":[{"ItemType":"/Recipe","CompletionDate":{"$date":{"$numberLong":"1000"}}}],"LastInventorySync":123}"""), "fixture", true));
            var runtime = new CompanionRuntime(directory, true);
            var lockReleased = false;
            runtime.NotificationRequested += (_, _) =>
            {
                lockReleased = Task.Run(runtime.GetSettings).Wait(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("Simulated desktop notification failure");
            };
            try
            {
                runtime.Start(watchGame: false);
                Check(SpinWait.SpinUntil(() => J.A(runtime.Get("state", new Dictionary<string, string>()), "events")
                    .Any(e => J.S(e, "text")?.StartsWith("Notification delivery failed:", StringComparison.Ordinal) == true), TimeSpan.FromSeconds(10)), "Notification failure was not reported");
                Check(lockReleased, "Desktop callback ran while the account lock was held");
            }
            finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }));
        test("Platform changes reuse bounded market clients while existing views remain valid", () => InDirectory(directory =>
        {
            var runtime = new CompanionRuntime(directory, true);
            try
            {
                var pc = runtime.View().Market;
                foreach (var platform in new[] { "ps4", "xb1", "swi", "pc", "ps4", "pc" })
                    runtime.Post("settings", new JsonObject { ["platform"] = platform }, CancellationToken.None).GetAwaiter().GetResult();
                Check(ReferenceEquals(pc, runtime.View().Market), "Returning to a platform replaced its still-used market client.");
                Check(J.S(runtime.GetSettings(), "platform") == "pc", "Platform setting was not persisted.");
            }
            finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }));
        test("Compositor bindings normalize modifiers and replace only the managed Lua block", () =>
        {
            Check(CompositorBindings.Line("sway", "Win+Alt+F9", "curl") == "bindsym Mod4+Mod1+F9 exec curl", "Sway modifiers differ");
            Check(CompositorBindings.Line("hyprland", "Ctrl+F10", "curl") == "bind = CTRL, F10, exec, curl", "Hyprland modifiers differ");
            var line = CompositorBindings.Line("hyprland-lua", "Super+F9", "curl");
            Check(line == "hl.bind(\"SUPER + F9\", hl.dsp.exec_cmd(\"curl\"))", "Lua binding differs");
            var initial = CompositorBindings.MergeLua("-- User configuration\n", [line]);
            var again = CompositorBindings.MergeLua(initial.Text, [line]);
            Check(initial.Added && !again.Added && again.Text == initial.Text, "Lua installation is not idempotent");
            var updated = CompositorBindings.MergeLua(initial.Text + "-- User footer\n", []);
            Check(updated.Text.StartsWith("-- User configuration", StringComparison.Ordinal) && updated.Text.EndsWith("-- User footer\n", StringComparison.Ordinal) && !updated.Text.Contains("hl.bind"), "Disabling keys changed user configuration");
            try { CompositorBindings.MergeLua("-- >>> warframeproject hotkeys (managed)", []); throw new Exception("Broken managed block accepted"); } catch (InventoryException) { }
        });
        test("Historical login preserves selected account and inferred identity retains inventory", () => InDirectory(directory =>
        {
            var logPath = Path.Combine(directory, "EE.log");
            File.WriteAllText(logPath, "Sys [Info]: Logged in FixturePlayer\nFixturePlayer 0123456789abcdef01234567\n");
            var store = new StateStore(Path.Combine(directory, "state.sqlite"));
            store.Write("registry", new RegistryState { Active = "view", Switched = J.Now, Accounts = [new() { Key = "view", Name = "Viewed" }, new() { Key = "game", Name = "FixturePlayer" }] });
            var settings = Settings.Defaults(); settings["ee_log_path"] = logPath; settings["ocr_enabled"] = false; store.Write("settings", settings);
            var snapshots = new SnapshotStore(Path.Combine(directory, "companion.sqlite"));
            snapshots.Commit(InventoryParser.Parse(Encoding.UTF8.GetBytes("""{"MiscItems":[{"ItemType":"/Fixture","ItemCount":7}],"LastInventorySync":123}"""), "game", true));
            var runtime = new CompanionRuntime(directory, true);
            try
            {
                runtime.Start();
                var state = runtime.Get("state", new Dictionary<string, string>());
                Check(J.S(state["account"], "key") == "view", "Historical login overwrote the selected account");
                var game = J.A(state, "accounts").Single(a => J.S(a, "key") == "game");
                Check(J.S(game, "id") == "0123456789abcdef01234567", "Certain log identity was not linked");
                Check(snapshots.Current("0123456789abcdef01234567")?.Counts.GetValueOrDefault("/Fixture") == 7, "Linking hid the previous inventory");
            }
            finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }));
        test("Log tail preserves split UTF-8 and suppresses historical reward events", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wfc-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "EE.log");
                File.WriteAllText(path, "OpenVoidProjectionRewardScreen\n");
                var log = new GameLog(); var signals = new List<LogSignal>(); log.Signal += signals.Add;
                log.Poll(path);
                Check(signals.Count == 0, "Historical reward screen was replayed");
                var login = Encoding.UTF8.GetBytes("Sys [Info]: Logged in Renée\n");
                var split = Array.IndexOf(login, (byte)0xc3) + 1;
                using (var append = new FileStream(path, FileMode.Append)) append.Write(login.AsSpan(0, split));
                log.Poll(path);
                Check(signals.Count == 0, "Incomplete line emitted a login");
                using (var append = new FileStream(path, FileMode.Append)) append.Write(login.AsSpan(split));
                log.Poll(path);
                Check(signals.Single().Value == "Renée", "Split UTF-8 player name was corrupted");
                File.AppendAllText(path, "OpenVoidProjectionRewardScreen\n"); log.Poll(path);
                Check(signals.Last().Kind == "rewards", "New reward screen was suppressed");
                var count = signals.Count;
                File.AppendAllText(path, "ProjectionRewardChoice.lua: Got rewards\n"); log.Poll(path);
                Check(signals.Count == count, "One screen triggered duplicate reads");
                File.Move(path, path + ".old");
                File.WriteAllText(path, "Sys [Info]: Logged in Replacement\nOpenVoidProjectionRewardScreen\n" + new string(' ', 400) + "\n");
                log.Poll(path);
                Check(signals.Count == count + 1 && signals.Last().Value == "Replacement", "Replacement file identity was missed or historical rewards replayed");
            }
            finally { Directory.Delete(directory, true); }
        });
        test("Compositor geometry preserves negative monitor origins and nested Sway windows", () =>
        {
            var hypr = JsonNode.Parse("""[{"class":"steam_app_230410","title":"Game","at":[-1920,80],"size":[1920,1080]}]""")!;
            Check(LinuxCompositor.Hyprland(hypr) == new GameRectangle(-1920, 80, 1920, 1080), "Hyprland geometry changed");
            var sway = JsonNode.Parse("""{"nodes":[{"floating_nodes":[{"name":"Warframe","rect":{"x":200,"y":30,"width":1280,"height":720}}]}]}""")!;
            Check(LinuxCompositor.Sway(sway) == new GameRectangle(200, 30, 1280, 720), "Sway geometry missing");
        });
        test("Non-OCR riven reader decodes integer rolls using current export definitions", () =>
        {
            const string type = "/Lotus/Upgrades/Mods/Randomized/Test"; var catalog = new Catalog(); catalog.Items["/Gun"] = new GameItem { Uid = "/Gun", Name = "Test Gun", Kind = "Rifle", Category = "Primary", Disposition = 1 };
            catalog.UpgradeExport = new JsonObject { [type] = JsonNode.Parse("""{"baseDrain":10,"fusionLimit":8,"upgradeEntries":[{"tag":"Damage","canBeBuff":true,"upgradeValues":[{"value":0.01,"locTag":"damage"}]},{"tag":"Crit","canBeBuff":true,"upgradeValues":[{"value":0.01,"locTag":"crit"}]},{"tag":"Zoom","canBeCurse":true,"upgradeValues":[{"value":0.01,"locTag":"zoom"}]}]}""") };
            catalog.Dictionary = JsonNode.Parse("""{"damage":"Damage","crit":"Critical Chance","zoom":"Zoom"}""");
            var payload = new JsonObject { ["ItemType"] = type, ["UpgradeFingerprint"] = JsonNode.Parse("""{"compat":"/Gun","lvl":8,"buffs":[{"Tag":"Damage","Value":1073741823},{"Tag":"Crit","Value":0}],"curses":[{"Tag":"Zoom","Value":0}],"Token":"must-not-escape"}""") };
            var result = FingerprintRivenReader.Read(catalog, payload); var rows = J.A(result, "stats").ToArray();
            Check(rows.Length == 3 && J.S(result, "source") == "game fingerprint", "Direct riven result missing");
            Check(J.D(rows[0]["value"]) == 122.5 && J.D(rows[2]["value"]) == -40.1, "Decoded stat values differ");
            Check(J.S(rows[0], "grade") == "A+" && J.S(rows[2], "grade") == "A+", "Roll polarity grading differs");
            Check(!result.ToJsonString().Contains("must-not-escape"), "Unrecognized fingerprint fields escaped");
            payload["UpgradeFingerprint"]!["buffs"]![0]!["Value"] = -1;
            try { FingerprintRivenReader.Read(catalog, payload); throw new Exception("Invalid roll accepted"); } catch (InventoryException) { }
        });
        test("Memory selection prefers the complete owned capture and joins adjacent regions", () =>
        {
            var snapshot = InventoryParser.Parse(Encoding.UTF8.GetBytes("{\"MiscItems\":[],\"LastInventorySync\":100}"), "fixture");
            var whole = new MemoryCapture(snapshot, ["MiscItems", "Suits"]); var fragment = new MemoryCapture(snapshot with { SourceTimestamp = 200 }, ["MiscItems"]);
            Check(ReferenceEquals(whole, MemoryProvider.Prefer(whole, fragment, c => c.Sections.Contains("Suits"))), "Newer fragment displaced complete capture");
            Check(MemoryProvider.JoinRegions([(100, 50), (150, 20), (200, 30)]).SequenceEqual([(100L, 70L), (200L, 30L)]), "Adjacent regions were not joined");
        });
        test("Update discovery excludes Rust assets, drafts and older builds", () =>
        {
            var release = JsonNode.Parse("""{"tag_name":"v0.10.0","html_url":"https://github.com/Lenalein2001/Warframe-App/releases/tag/v0.10.0","assets":[{"name":"WarframeCompanion-win-x64.zip"}]}""")!;
            Check(UpdateService.NewerRelease(release, "0.9.0") != null, "New C# release missing");
            Check(UpdateService.NewerRelease(release, "0.11.0") == null, "Older release offered");
            release["assets"] = new JsonArray(); Check(UpdateService.NewerRelease(release, "0.9.0") == null, "Rust release offered");
        });
        test("Settings reject fractional values and allow disabling hotkeys", () =>
        {
            var settings = Settings.Patch(Settings.Defaults(), JsonNode.Parse("{\"hotkey_rewards\":\"\",\"hotkey_riven\":\"\"}")!);
            Check(J.S(settings, "hotkey_rewards") == null, "Blank binding was not retained");
            foreach (var value in new[] { "0.1", "-1", "4294967296", "true", "null" })
            {
                try { Settings.Patch(settings, JsonNode.Parse("{\"overlay_seconds\":" + value + "}")!); throw new Exception("Invalid setting accepted"); }
                catch (InventoryException) { }
            }
        });
        test("Native fuzzy matching preserves directional ties, Unicode and autojunk", () =>
        {
            Check(NativeEngine.Similarity("tide", "diet") == .25, "Directional tie changed");
            Check(NativeEngine.Similarity("diet", "tide") == .5, "Reverse tie changed");
            Check(NativeEngine.Similarity("é猫x", "é猫y") == 2.0 / 3, "UTF8 was counted as bytes");
            Check(NativeEngine.Similarity("b" + new string('a', 250), "c" + new string('a', 250)) == 0, "Autjunk was lost");
        });
        test("Legacy migration preserves identifiers, manual state and reward history", () => InDirectory(directory =>
        {
            var source = Path.Combine(directory, "old"); var folder = Path.Combine(source, "accounts", "player_key"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(source, "accounts.json"), """{"active":"player_key","accounts":[{"key":"player_key","name":"Fixture","badge":[13,15]}]}""");
            var inventory = """{"counts":{"/Item_With_Underscores":7},"equipment":{},"xp":{"/Weapon_With_Underscores":900000},"progress":{"nodes":{},"intrinsics":{"LPS_PILOTING":5}},"imported":100,"synced_at":90,"mastery_rank":13}""";
            File.WriteAllText(Path.Combine(folder, "inventory.json"), inventory);
            File.WriteAllText(Path.Combine(folder, "memscan_sections.json"), """{"captures":10,"seen":{"MiscItems":10,"Suits":9,"Upgrades":8,"privateField":10}}""");
            File.WriteAllText(Path.Combine(folder, "manual.json"), """{"counts":{"/Item_With_Underscores":[8,110]},"mastered":{},"pinned":["/Item_With_Underscores"],"tasks":[],"goals":["/Goal"]}""");
            var snapshots = new SnapshotStore(Path.Combine(directory, "snapshot.sqlite")); var migrated = LegacyMigration.Load(source, source, snapshots)!.Value;
            Check(migrated.Registry.Accounts.Single().Manual.Counts["/Item_With_Underscores"].Count == 8, "Manual count lost");
            Check(migrated.Registry.Accounts.Single().Manual.Tasks.Single().Uid == "/Goal", "Legacy goal lost");
            Check(migrated.Registry.Accounts.Single().ExpectedMemorySections.SequenceEqual(new[] { "MiscItems", "Suits" }), "Learned inventory completeness was lost or unrecognized fields retained");
            var current = snapshots.Current("player_key")!; Check(current.Counts["/Item_With_Underscores"] == 7 && current.Progress!.Intrinsics["LPS_PILOTING"] == 5, "Identifiers changed");
            Check(File.ReadAllText(Path.Combine(folder, "inventory.json")) == inventory, "Migration changed source");
            var historyPath = Path.Combine(source, "rewards.jsonl"); File.WriteAllText(historyPath, "broken\n" + """{"t":100,"account":"player_key","drops":[{"name":"Part","uid":"/Part","price":12,"needed":true}],"private":"discard"}""" + "\n");
            var state = new StateStore(Path.Combine(directory, "state.sqlite")); var history = new RewardHistory(state); history.ImportLegacy(historyPath);
            Check(J.A(history.Read("player_key", 0), "entries").Count() == 1, "History migration failed");
            Check(!history.Entries.ToJsonString().Contains("discard"), "Unrecognized history field retained");
            Check(J.A(history.Read("another", 0), "entries").Count() == 0, "History leaked across accounts");
            history.ImportLegacy(historyPath); Check(history.Entries.Count == 1, "History imported twice");
        }));
        test("Loopback HTTP serves preserved UI and enforces validation and persistence", () => InDirectory(directory => HttpTest(directory).GetAwaiter().GetResult()));
    }
    public static void RegisterOcr(Action<string, Action> test)
    {
        test("Installed Tesseract reads synthetic reward and riven images through native preprocessing", () =>
        {
            Check(ScreenCapture.Which("tesseract") != null, "Tesseract and English data are required for --ocr");
            using var bitmap = new SkiaSharp.SKBitmap(1000, 300, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
            using var canvas = new SkiaSharp.SKCanvas(bitmap); canvas.Clear(SkiaSharp.SKColors.Black);
            using var typeface = SkiaSharp.SKTypeface.FromFamilyName(OperatingSystem.IsWindows() ? "Arial" : "DejaVu Sans"); using var font = new SkiaSharp.SKFont(typeface, 38);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, IsAntialias = true };
            canvas.DrawText("Ash Prime Systems Blueprint", 20, 60, SkiaSharp.SKTextAlign.Left, font, paint);
            var text = ScreenCapture.Text(bitmap, new(0, 0, 1000, 100), 6, 0, CancellationToken.None).GetAwaiter().GetResult();
            Check(OcrReader.Match(text, ["Ash Prime Systems Blueprint", "Braton Prime Barrel"]).Name == "Ash Prime Systems Blueprint", "Reward image was not recognized");
            Check(bitmap.GetPixel(0, 0) == SkiaSharp.SKColors.Black, "Preprocessing mutated original frame");
            canvas.Clear(SkiaSharp.SKColors.Black); var lines = new[] { "Boltor Visi", "+165% Damage", "+120% Critical Chance", "-45% Zoom" };
            for (var i = 0; i < lines.Length; i++) canvas.DrawText(lines[i], 20, 55 + i * 60, SkiaSharp.SKTextAlign.Left, font, paint);
            text = ScreenCapture.Text(bitmap, new(0, 0, 1000, 300), 6, 0, CancellationToken.None).GetAwaiter().GetResult();
            var riven = OcrReader.ParseRiven(text, ["Boltor", "Braton"]); Check(riven.Weapon == "Boltor" && riven.Stats.Length == 3, "Riven image was not recognized");
        });
    }
    public static void RegisterRewardImage(Action<string, Action> test, string path, string cachePath, string[]? expected)
    {
        test("External reward screenshot through native layout and OCR", () =>
        {
            using var cache = new CachedJson(cachePath, true); var catalog = new CatalogLoader(cache).Load().GetAwaiter().GetResult();
            using var bitmap = SkiaSharp.SKBitmap.Decode(path) ?? throw new Exception("Image could not be decoded");
            var layout = NativeEngine.Layout(bitmap, null); Console.WriteLine("Screenshot layout: " + System.Text.Json.JsonSerializer.Serialize(layout));
            Check(layout != null, "Reward layout was not detected");
            var names = catalog.Relics.Values.SelectMany(r => r.Rewards.Values).Select(r => r.Name).Distinct().ToArray(); var recognized = new List<string?>();
            for (var slot = 0; slot < layout!.Cards; slot++)
            {
                var width = 242 * layout.Unit; var left = bitmap.Width / 2.0 - layout.Cards * width / 2 + slot * width;
                var rect = new SkiaSharp.SKRectI((int)left, (int)(bitmap.Height / 2.0 - 150 * layout.Unit), (int)(left + width), (int)(bitmap.Height / 2.0 - 78 * layout.Unit));
                (string? Name, double Score) best = (null, 0);
                foreach (var variant in new[] { 2, 3, 4, 0 })
                {
                    var read = ScreenCapture.Text(bitmap, rect, 6, variant, CancellationToken.None).GetAwaiter().GetResult(); var match = OcrReader.Match(read, names);
                    Console.WriteLine($"Card {slot + 1}, variant {variant}: {match.Name ?? "unknown"} ({match.Score:F3})");
                    if (match.Score > best.Score) best = match; if (best.Score >= .98) break;
                }
                recognized.Add(best.Name);
            }
            if (expected != null) Check(recognized.SequenceEqual(expected), "Recognized rewards do not match expected slot order");
        });
    }
    public static void RegisterLiveMemory(Action<string, Action> test, string directory)
    {
        test("Live game watcher commits a capture to an isolated account store", () => InDirectory(target =>
        {
            var source = new StateStore(Path.Combine(directory, "state.sqlite"));
            var registry = source.Read<RegistryState>("registry") ?? throw new Exception("Known accounts are required.");
            var state = new StateStore(Path.Combine(target, "state.sqlite"));
            state.Write("registry", registry);
            var settings = Settings.Defaults(); settings["memory_scan_enabled"] = true; settings["ocr_enabled"] = false;
            state.Write("settings", settings);
            var sourceSnapshots = new SnapshotStore(Path.Combine(directory, "companion.sqlite"));
            var snapshots = new SnapshotStore(Path.Combine(target, "companion.sqlite"));
            foreach (var account in registry.Accounts)
                if (sourceSnapshots.Current(account.Id ?? account.Key) is { } saved) snapshots.Commit(saved);
            var runtime = new CompanionRuntime(target, true);
            try
            {
                runtime.Start();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(60))
                {
                    if (registry.Accounts.Any(a => snapshots.Current(a.Id ?? a.Key)?.Provider == "game memory")) break;
                    if (runtime.ScanError != null) throw new Exception(runtime.ScanError);
                    Thread.Sleep(250);
                }
                Check(registry.Accounts.Any(a => snapshots.Current(a.Id ?? a.Key) is { Provider: "game memory", Complete: true, IdentityVerified: true }), "Game watcher did not promote a verified capture.");
                Check(snapshots.IntegrityCheck() == "ok", "Live capture damaged the isolated database.");
                Console.WriteLine($"Live watcher saved verified inventory in {watch.Elapsed.TotalSeconds:F2} seconds; original application data was untouched.");
            }
            finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }));
        test("Live native game-memory capture identifies the expected account", () =>
        {
            var runtime = new CompanionRuntime(directory, true);
            try
            {
                var view = runtime.View();
                var current = view.Inventory ?? throw new Exception("An isolated known inventory is required for ownership comparison.");
                var pid = MemoryProvider.GamePid() ?? throw new Exception("Warframe is not running.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                bool Accept(MemoryCapture capture) => (capture.Snapshot.IdentityVerified || LegacyMigration.SameOwner(capture.Snapshot.Xp, view.Profile?.Xp ?? current.Xp) == true)
                    && new[] { "MiscItems", "Suits", "Recipes", "XPInfo" }.All(capture.Sections.Contains);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var diagnostics = new HashSet<string>();
                var result = MemoryProvider.Scan(pid, current.AccountId, deadline.Token, Accept, message => { if (diagnostics.Count < 12 && diagnostics.Add(message)) Console.WriteLine(message); });
                Check(Accept(result), "Live capture lacks verified ownership or required inventory sections.");
                Console.WriteLine($"Live capture: {result.Snapshot.Counts.Count} stacks, {result.Sections.Length} sections, {watch.Elapsed.TotalSeconds:F2} seconds; no capture was persisted.");
            }
            finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }
    private static void InDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "companion-integration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static async Task HttpTest(string directory)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var server = await CompanionServer.Start(directory, offline: true, port: port, watchGame: false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url), Timeout = TimeSpan.FromSeconds(10) };
        async Task<JsonNode> Get(string path) => JsonNode.Parse(await client.GetStringAsync(path))!;
        async Task<JsonNode> Post(string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            using var response = await client.PostAsync("/api/" + path, new StringContent(json, Encoding.UTF8, "application/json"));
            Check(response.StatusCode == status, path + " returned " + response.StatusCode); return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
        foreach (var route in new[] { "state", "settings", "mastery", "primes", "inventory", "foundry", "relics", "trade", "find?q=foo", "find-task?q=foo", "tasks", "world", "fissures", "rivens", "history", "rewards", "riven/result", "hotkeys", "standing", "search?q=foo" }) await Get("/api/" + route);
        Check(J.A(await Get("/api/state"), "accounts").Count() == 0, "Isolated server observed the real game");
        var assembly = typeof(CompanionServer).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)))
        {
            await using var embedded = assembly.GetManifestResourceStream(name)!; using var memory = new MemoryStream(); await embedded.CopyToAsync(memory);
            Check((await client.GetByteArrayAsync("/" + name[4..])).SequenceEqual(memory.ToArray()), "UI asset differs: " + name);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/state")) { request.Headers.Host = "evil.example:" + port; using var reply = await client.SendAsync(request); Check(reply.StatusCode == HttpStatusCode.Forbidden, "Foreign Host accepted"); }
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/clear")) { request.Headers.Add("Origin", "https://evil.example"); using var reply = await client.SendAsync(request); Check(reply.StatusCode == HttpStatusCode.Forbidden, "Foreign Origin accepted"); }
        await Post("settings", "{\"port\":0.1}", HttpStatusCode.BadRequest);
        await Post("settings", "{\"memory_scan_enabled\":true,\"ocr_enabled\":false}");
        var settings = await Get("/api/settings"); Check(J.B(settings["memory_scan_enabled"]) && !J.B(settings["ocr_enabled"]), "Boolean settings could not be toggled");
        await Post("settings", "{\"memory_scan_enabled\":false,\"ocr_enabled\":true}");
        await Post("manual/count", "{\"uid\":\"/Part\",\"count\":1.2}", HttpStatusCode.BadRequest);
        await Post("account/set", "{\"id\":\"0123456789abcdef01234567\"}");
        await Post("account/set", "{\"key\":\"missing\",\"id\":\"0123456789abcdef01234567\"}", HttpStatusCode.BadRequest);
        await Post("profile/import", "{\"AccountId\":\"0123456789abcdef01234567\",\"DisplayName\":\"Fixture\",\"XPInfo\":[],\"PlayerLevel\":0}");
        await Post("profile/import", "{\"AccountId\":\"abcdef0123456789abcdef01\",\"XPInfo\":[]}", HttpStatusCode.BadRequest);
        var imported = await Post("inventory/import", "{\"MiscItems\":[{\"ItemType\":\"/Part\",\"ItemCount\":7}],\"Suits\":[],\"Recipes\":[],\"XPInfo\":[],\"LastInventorySync\":100}");
        Check(J.B(imported["promoted"]), "Complete import was not promoted");
        var partial = await Post("inventory/import", "{\"MiscItems\":[],\"LastInventorySync\":101}"); Check(!J.B(partial["promoted"]), "Partial import erased confirmed data");
        var store = new SnapshotStore(Path.Combine(directory, "companion.sqlite")); Check(store.Current("0123456789abcdef01234567")!.Counts["/Part"] == 7, "Confirmed count lost");
        await Post("inventory/clear", "{}"); Check(store.Current("0123456789abcdef01234567") == null, "Clear did not clear current pointer");
        Check(store.History("0123456789abcdef01234567").Count == 2, "Clear destroyed history");
        using var empty = await client.PostAsync("/api/window/show", null); Check(empty.IsSuccessStatusCode, "Empty hotkey POST failed");
    }
}
