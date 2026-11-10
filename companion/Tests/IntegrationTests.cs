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
            File.WriteAllText(Path.Combine(folder, "manual.json"), """{"counts":{"/Item_With_Underscores":[8,110]},"mastered":{},"pinned":["/Item_With_Underscores"],"tasks":[],"goals":["/Goal"]}""");
            var snapshots = new SnapshotStore(Path.Combine(directory, "snapshot.sqlite")); var migrated = LegacyMigration.Load(source, source, snapshots)!.Value;
            Check(migrated.Registry.Accounts.Single().Manual.Counts["/Item_With_Underscores"].Count == 8, "Manual count lost");
            Check(migrated.Registry.Accounts.Single().Manual.Tasks.Single().Uid == "/Goal", "Legacy goal lost");
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
