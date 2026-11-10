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
