using System.Text;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;
using WarframeCompanion.Services;

public static class ServiceTests
{
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static JsonNode Json(string value) => JsonNode.Parse(value)!;
    public static void Register(Action<string, Action> test)
    {
        test("Overlay layout keeps four ordered panels aligned at game scale and monitor origin", () =>
        {
            var request = new OverlayRequest("rewards", Json("""{"cards":4}"""), -1920, 80, 1920, 1080, 1, 15, false);
            var boxes = OverlayLayout.Boxes(request);
            Equal(4, boxes.Length); Equal(-1440, boxes[0].X); Equal(568, boxes[0].Y);
            Equal(234, boxes[0].Width); Equal(86, boxes[0].Height);
            for (var i = 1; i < boxes.Length; i++) Equal(242, boxes[i].X - boxes[i - 1].X);
            var above = OverlayLayout.Boxes(request with { Above = true });
            Equal(208, above[0].Y);
            Equal(true, above.Zip(above.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left));
        });
        test("Riven constants, count inference, negative grades and sign conflicts", () =>
        {
            Equal(50, RivenService.Data.Stats.Length); Equal(18, RivenService.Data.Stats.Count(s => s.Spliced)); Equal(50, RivenService.Data.Slugs.Count);
            var item = new GameItem { Uid = "/Gun", Name = "Gun", Kind = "Rifle", Category = "Primary", RivenClass = "rifle", Disposition = 1.4 };
            Equal((3, 0, 1), RivenService.Infer(item, [("Ammo Maximum", 56.9, false), ("Critical Chance", 167.4, false)]));
            Equal("A+", RivenService.Roll(item, "Zoom", -59.99 * 1.4 * .75 * .92, 3, 1, true)!.Grade);
            Equal(true, RivenService.Roll(item, "Damage", -100, 3, 1, true)!.SignConflict);
            Equal(true, RivenService.Roll(item, "Damage", 500, 2, 0, false)!.OutOfRange);
        });
        test("Riven OCR disambiguates wrapped stats, weapon boundaries and faction multipliers", () =>
        {
            Equal("Slide Critical Chance", OcrReader.StatAfter("Critical Chance for Slide Attack"));
            Equal("Magazine Capacity", OcrReader.StatAfter("Magazine . ~ v & Capacity"));
            var read = OcrReader.ParseRiven("Boltor Visi\n+165% Damage\nx1.45 Damage to Corpus\n-90% Recoil", ["Bo", "Bolto", "Boltor"]);
            Equal("Boltor", read.Weapon); Equal(3, read.Stats.Length); Equal(false, read.Stats[2].Negative); Equal(45.0, Math.Round(read.Stats[1].Value, 3));
        });
        test("Native bounded marker matching and image conversion agree with contracts", () =>
        {
            Equal(2, NativeEngine.Find([1, 2, 1, 2, 3], 5, [1, 2, 3])); Equal(-1, NativeEngine.Find([1, 2], 2, [1, 2, 3]));
            using var bitmap = new SkiaSharp.SKBitmap(1920, 1080, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque); bitmap.Erase(SkiaSharp.SKColors.Black);
            Equal<RewardLayout?>(null, NativeEngine.Layout(bitmap, 1));
            for (var count = 1; count <= 4; count++)
            {
                bitmap.Erase(SkiaSharp.SKColors.Black); var left = 960 - count * 242 / 2;
                for (var slot = 0; slot < count; slot++) for (var y = 476; y <= 479; y++) for (var x = left + slot * 242 + 5; x < left + (slot + 1) * 242 - 5; x++) bitmap.SetPixel(x, y, new SkiaSharp.SKColor(110, 110, 110));
                Equal(count, NativeEngine.Layout(bitmap, 1)!.Cards); Equal(OcrReader.Layout(bitmap, 1), NativeEngine.Layout(bitmap, 1));
            }
        });
        test("Log parsing strips platform glyphs and does not invent identity", () =>
        {
            var signals = GameLog.Parse("0.1 Sys [Info]: Logged in Test\ue000").ToArray(); Equal("Test", signals.Single().Value); Equal<string?>(null, signals.Single().Id);
            Equal("0123456789abcdef01234567", GameLog.Parse("Logged in Test (0123456789abcdef01234567)").Single().Id);
            Equal(0, GameLog.Parse("Remote player 0123456789abcdef01234567 joined").Count());
            Equal("sync", GameLog.Parse("EndOfMatch.lua: Mission Succeeded").Single().Kind);
            Equal("duviri-item", GameLog.Parse("MapRedux.lua: /Lotus/Powersuits/Paladin/Paladin generated as an unowned Cave selection for category: 3").Single().Kind);
            Equal("rewards", GameLog.Parse("ProjectionRewardChoice.lua: Got rewards").Single().Kind);
            const string ownId = "0123456789abcdef01234567";
            Equal(true, GameLog.Candidates("Test\ue000 " + ownId, "Test").Single().Sure);
            Equal(0, GameLog.Candidates("Test2 " + ownId, "Test").Count());
            Equal(false, GameLog.Candidates("Test " + ownId + " abcdef0123456789abcdef01", "Test").First().Sure);
            Equal(true, GameLog.Candidates("VoidProjections: Sending reward info from " + ownId + " to host!", null).Single().Sure);
        });
        test("Memory JSON recovery never persists unrecognized secret fields", () =>
        {
            var bytes = Encoding.UTF8.GetBytes("\"MiscItems\":[],\"Suits\":[],\"LastInventorySync\":100,\"Token\":\"private-test\"}trailing"); var marker = bytes.AsSpan().IndexOf("\"LastInventorySync\""u8);
            var capture = MemoryProvider.Decode(bytes, marker, "fixture")!; Equal(100.0, capture.Snapshot.SourceTimestamp); Equal(false, capture.Snapshot.Complete);
            Equal(false, System.Text.Json.JsonSerializer.Serialize(capture.Snapshot).Contains("private-test", StringComparison.Ordinal));
            foreach (var invalid in new[] { "{\"Nested\":{\"MiscItems\":[],\"LastInventorySync\":123}}", "{\"MiscItems\":[],\"Nested\":{\"LastInventorySync\":123}}", "broken,\"MiscItems\":[],\"LastInventorySync\":123" })
            {
                var raw = Encoding.UTF8.GetBytes(invalid);
                Equal<MemoryCapture?>(null, MemoryProvider.Decode(raw, raw.AsSpan().IndexOf("\"LastInventorySync\""u8), "fixture"));
            }
            var suffixed = Encoding.UTF8.GetBytes(",\"LastInventorySync\":123,\"MiscItems\":[]}");
            Equal(123.0, MemoryProvider.Decode(suffixed, suffixed.AsSpan().IndexOf("\"LastInventorySync\""u8), "fixture")!.Snapshot.SourceTimestamp);
            Equal<MemoryCapture?>(null, MemoryProvider.Decode(suffixed, -1, "fixture"));
        });
        test("Artwork rejects arbitrary URLs and path traversal", () =>
        {
            Equal(true, ArtService.Allowed("https://cdn.warframestat.us/img/test.png")); Equal(false, ArtService.Allowed("https://cdn.warframestat.us.evil.test/img/test.png"));
            Equal(false, ArtService.Allowed("https://browse.wf/Lotus/Interface/%2e%2e/private.png")); Equal(false, ArtService.Allowed("http://127.0.0.1/private.png"));
            Equal("https://warframe.market/static/assets/sub_icons/warframe/generic_helmet_128x128.png", MarketService.SlotGlyph("Warframes", "/TenguHelmetComponent"));
            Equal("https://warframe.market/static/assets/sub_icons/blueprint_128x128.png", MarketService.SlotGlyph("Warframes", "/IronframeBlueprint"));
            Equal("Familial Debt-bond", CatalogLoader.Clean("FAMILIAL DEBT-BOND"));
            Equal(true, ArtService.AllowedRedirect("https://raw.githubusercontent.com/wfcd/warframe-items/master/data/img/GyrePrime.png"));
            Equal(false, ArtService.AllowedRedirect("https://raw.githubusercontent.com/other/repo/main/private.png"));
        });
        test("Catalog resolves equipment components, aliases, relic rewards and Plexus", () =>
        {
            var cat = Fixture(); Equal(3, cat.Items.Count); Equal(40L, cat.Items["/Mech"].MaxRank);
            Equal("/Lotus/Types/Recipes/WarframeRecipes/TestHelmetBlueprint", cat.Aliases["/Lotus/Types/Recipes/WarframeRecipes/TestHelmetComponent"]);
            Equal("Test Prime Neuroptics", cat.Parts["/Lotus/Types/Recipes/WarframeRecipes/TestHelmetBlueprint"].Name);
            Equal(10.0, cat.Relics["Lith T1"].Rewards.Values.Single().Chance["Radiant"]);
        });
        test("Relic export repair reconstructs rewards when WFCD omits them", () =>
        {
            var raw = new Dictionary<string, JsonNode>
            {
                ["Relics"] = Json("""[{"uniqueName":"/Relic","name":"Lith T1 Intact"}]"""),
                ["ExportRelics"] = Json("""{"/Relic":{"era":"Lith","category":"T1","quality":"VPQ_BRONZE","rewardManifest":"/Table"}}"""),
                ["ExportRewards"] = Json("""{"/Table":[[{"type":"/Lotus/StoreItems/Recipes/FrameBlueprint","rarity":"RARE"}]]}"""),
                ["drops_relics"] = Json("""{"relics":[{"tier":"Lith","relicName":"T1","state":"Intact","rewards":[{"itemName":"Test Prime Blueprint","chance":2}]}]}""")
            };
            CatalogLoader.RepairRelics(raw); var cat = Catalog.Build(raw);
            Equal("Test Prime Blueprint", cat.Relics["Lith T1"].Rewards.Values.Single().Name);
            Equal(2.0, cat.Relics["Lith T1"].Rewards.Values.Single().Chance["Intact"]);
        });
        test("Mastery thresholds include Legendary ranks and negative XP is clamped", () =>
        {
            Equal((0L, 0L, 2500L), GameView.RankFor(0)); Equal((30L, 2250000L, 2397500L), GameView.RankFor(2250000));
            Equal((31L, 2397500L, 2545000L), GameView.RankFor(2397500));
        });
        test("Drop index retains mission rotation, bounty stage and conditional enemy chances", () =>
        {
            var drops = DropIndex.Build(new Dictionary<string, JsonNode>
            {
                ["missionRewards"] = Json("""{"missionRewards":{"Venus":{"Fossa":{"gameMode":"Assassination","rewards":{"A":[{"itemName":"Rhino Chassis Blueprint","chance":38.72}]}}}}}"""),
                ["modLocations"] = Json("""{"modLocations":[{"modName":"Vitality","enemies":[{"enemyName":"Lancer","enemyModDropChance":3,"chance":22}]}]}""")
            }, 11);
            var drop = drops.ForName("Rhino Chassis", true).Single(); Equal("Venus · Fossa", drop.Location); Equal("A", drop.Rotation);
            Equal(3.0, drops.ForName("vitality").Single().EnemyDropChance); Equal(2, drops.Available);
        });
        test("Market empty order sides remain unknown and corrupt prices fail", () =>
        {
            var price = MarketService.ParseOrders(Json("""{"data":{"sell":[{"platinum":20},{"platinum":10},{"platinum":30},{"platinum":100}],"buy":[]}}"""), 42);
            Equal<long?>(10, price.Sell); Equal<double?>(20, price.SellAvg); Equal<long?>(null, price.Buy);
            try { MarketService.ParseOrders(Json("""{"data":{"sell":[{"platinum":-1}],"buy":[]}}"""), 42); throw new Exception("Invalid price accepted"); } catch (IOException) { }
        });
        test("Manual timestamps, mastery, farming resources and details match source semantics", () => WithView((view, manual) =>
        {
            Equal(2L, view.Count("/Lotus/Types/Recipes/WarframeRecipes/TestHelmetBlueprint"));
            manual.Counts["/Cell"] = new(999, 90); Equal(100L, view.Count("/Cell"));
            manual.Counts["/Cell"] = new(999, 110); Equal(999L, view.Count("/Cell"));
            Equal("mastered", view.Status(view.Catalog.Items["/Mech"]).Status);
            var detail = view.Detail("/Frame")!; Equal("item", J.S(detail, "kind")); Equal(1, J.A(detail, "parts").Count());
            var plan = new FarmingPlanner(view).Plan(view.Catalog.Items["/Frame"]);
            Equal(0L, plan.Parts[0].Builds); Equal(0, plan.Materials.Count); // crafted component is already present
            Equal(21L, FarmingPlanner.ArcaneCopies(5));
        }));
        test("Search token prefixes and source-backed collection contracts", () => WithView((view, _) =>
        {
            Equal<int?>(2, GameView.MatchRank("Test Prime Neuroptics", "test neu"));
            Equal(1L, J.N(view.Collection(), "setsComplete")); Equal(1, J.A(view.Primes()).Count());
            Equal(1, J.A(view.Relics(true)).Count()); Equal(0L, J.N(view.InventoryRows()["currencies"], "platinum"));
        }));
    }
    private static Catalog Fixture() => Catalog.Build(new Dictionary<string, JsonNode>
    {
        ["Warframes"] = Json("""[{"uniqueName":"/Frame","name":"Test Prime","masterable":true,"isPrime":true,"components":[{"uniqueName":"/Lotus/Types/Recipes/WarframeRecipes/TestHelmetComponent"}]},{"uniqueName":"/Mech","name":"Test Mech","masterable":true,"type":"Necramech","maxLevelCap":30}]"""),
        ["Resources"] = Json("""[{"uniqueName":"/Cell","name":"Orokin Cell"}]"""),
        ["Relics"] = Json("""[{"uniqueName":"/Relic","name":"Lith T1 Intact","vaulted":false,"rewards":[{"item":{"uniqueName":"/Lotus/Types/Recipes/WarframeRecipes/TestHelmetBlueprint","name":"Test Prime Neuroptics"},"rarity":"Rare","chance":2}]},{"uniqueName":"/RelicRadiant","name":"Lith T1 Radiant","rewards":[{"item":{"uniqueName":"/Lotus/Types/Recipes/WarframeRecipes/TestHelmetBlueprint","name":"Test Prime Neuroptics"},"rarity":"Rare","chance":10}]}]"""),
        ["ExportRecipes"] = Json("""{"/Lotus/Types/Recipes/WarframeRecipes/TestHelmetBlueprint":{"resultType":"/Lotus/Types/Recipes/WarframeRecipes/TestHelmetComponent","num":1,"buildPrice":15000,"ingredients":[{"ItemType":"/Cell","ItemCount":2}]}}""")
    });
    private static void WithView(Action<GameView, ManualState> action)
    {
        var temp = Path.Combine(Path.GetTempPath(), "companion-services-" + Guid.NewGuid().ToString("N"));
        var market = new MarketService(temp, "pc", true);
        try
        {
            var inv = InventoryParser.Parse(Encoding.UTF8.GetBytes("""{"MiscItems":[{"ItemType":"/Lotus/Types/Recipes/WarframeRecipes/TestHelmetComponent","ItemCount":1},{"ItemType":"/Lotus/Types/Recipes/WarframeRecipes/TestHelmetBlueprint","ItemCount":1},{"ItemType":"/Cell","ItemCount":100},{"ItemType":"/Relic","ItemCount":2}],"Suits":[{"ItemType":"/Mech","XP":1600000}],"PremiumCredits":0,"LastInventorySync":100}"""), "fixture", true);
            var manual = new ManualState(); action(new(Fixture(), inv, null, manual, market), manual);
        }
        finally { market.DisposeAsync().AsTask().GetAwaiter().GetResult(); Directory.Delete(temp, true); }
    }
}
