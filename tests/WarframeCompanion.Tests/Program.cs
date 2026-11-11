using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;

var tests = new List<(string, Action)>();
void Test(string name, Action test) => tests.Add((name, test));
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}");
}
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
InventorySnapshot Parse(string json, string account = "test", bool complete = false) =>
    InventoryParser.Parse(Encoding.UTF8.GetBytes(json), account, complete, DateTimeOffset.UnixEpoch);
var temp = Path.Combine(Path.GetTempPath(), "warframe-companion-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
SnapshotStore Store() => new(Path.Combine(temp, Guid.NewGuid() + ".sqlite"));
InventorySnapshot Snapshot(long time, long count = 5, bool complete = true, string account = "test") =>
    Parse($$"""{"MiscItems":[{"ItemType":"/P","ItemCount":{{count}}}],"LastInventorySync":{{time}}} """, account, complete);

Test("Rust parity: duplicate stacks, equipment XP, zero currency and Mongo dates", () =>
{
    var s = Parse("""
        {"MiscItems":[{"ItemType":"/P","ItemCount":3},{"ItemType":"/P","ItemCount":"2"}],
         "Suits":[{"ItemType":"/Ash","XP":10},{"ItemType":"/Ash","XP":900000}],
         "PendingRecipes":[{"ItemType":"/R","CompletionDate":{"$date":{"$numberLong":"1791032114000"}}}],
         "PremiumCredits":0,"LastInventorySync":{"$oid":"5f1e2d3c4b5a69788796a5b4"}}
        """);
    Equal(5L, s.Counts["/P"]); Equal(new Equipment(2, 900000), s.Equipment["/Ash"]);
    Equal<double?>(1791032114, s.Foundry[0].Done); Equal<long?>(0, s.Currencies.Platinum);
    Equal<double?>(0x5f1e2d3c, s.SourceTimestamp); Equal(null, s.Upgrades);
});
Test("Rust parity: ranked mods and arcanes remain separate from stacks", () =>
{
    var s = Parse("""
        {"MiscItems":[],"RawUpgrades":[{"ItemType":"/Flow","ItemCount":2}],
         "Upgrades":[{"ItemType":"/Arcane","UpgradeFingerprint":"{\"lvl\":5}"},
                     {"ItemType":"/Arcane","UpgradeFingerprint":{"lvl":2}},
                     {"ItemType":"/Riven","UpgradeFingerprint":"not json"}]}
        """);
    Equal("5,2", string.Join(',', s.Upgrades!["/Arcane"])); Equal(5L, s.Mods);
    Equal(2L, s.Counts["/Flow"]); Equal(0L, s.Upgrades["/Riven"][0]);
    Equal(true, s.Warnings.Any(w => w.Contains("fingerprint")));
});
Test("Wrapped object and JSON string inventory", () =>
{
    Equal(1L, Parse("""{"InventoryJson":"{\"Recipes\":[{\"ItemType\":\"/X\",\"ItemCount\":1}]}"}""").Counts["/X"]);
    Equal(0, Parse("""{"InventoryJson":{"MiscItems":[]}}""").Counts.Count);
});
Test("Missing, invalid and negative counts are rejected rather than converted to zero", () =>
{
    foreach (var json in new[] { "{}", "[]", "{", "{\"MiscItems\":null}", "{\"MiscItems\":{}}", "{\"MiscItems\":[],\"MiscItems\":[]}" })
        Throws<InventoryException>(() => Parse(json));
    foreach (var value in new[] { "-1", "1.2", "null", "true", "\"invalid\"", "9223372036854775808" })
        Throws<InventoryException>(() => Parse($$"""{"MiscItems":[{"ItemType":"/X","ItemCount":{{value}}}]}"""));
    Throws<InventoryException>(() => Parse("""{"MiscItems":[{"ItemType":"/X"}]}"""));
    Throws<InventoryException>(() => Parse("""{"MiscItems":[{"ItemType":"/X","ItemCount":9223372036854775807},{"ItemType":"/X","ItemCount":1}]}"""));
});
Test("Game serialization omits cosmetic zero counts and unranked syndicate titles", () =>
{
    var s = Parse("""{"MiscItems":[],"FlavourItems":[{"ItemType":"/Cosmetic"}],"Affiliations":[{"Tag":"Neutral","Standing":0}]}""");
    Equal(0L, s.Counts["/Cosmetic"]); Equal(0L, s.Standing!.Syndicates["Neutral"].Title);
    foreach (var value in new[] { "null", "-1", "1.2", "true" })
        Throws<InventoryException>(() => Parse($$"""{"MiscItems":[],"FlavourItems":[{"ItemType":"/Cosmetic","ItemCount":{{value}}}]}"""));
    Throws<InventoryException>(() => Parse("""{"MiscItems":[],"Affiliations":[{"Tag":"Neutral","Standing":0,"Title":null}]}"""));
});
Test("Identity verification and cross-account rejection", () =>
{
    Equal(true, Parse("""{"MiscItems":[],"AccountId":{"$oid":"test"}}""").IdentityVerified);
    Equal(false, Parse("""{"MiscItems":[]}""").IdentityVerified);
    Throws<InventoryException>(() => Parse("""{"MiscItems":[],"AccountId":"other"}"""));
    Throws<InventoryException>(() => Parse("""{"MiscItems":[],"AccountId":{}}"""));
    Throws<InventoryException>(() => Parse("""{"MiscItems":[]}""", "../other"));
});
Test("Progress and standing preserve signed ranks and zero daily limits", () =>
{
    var s = Parse("""
        {"MiscItems":[],"Missions":[{"Tag":"Node","Completes":1},{"Tag":"Node","Tier":1}],
         "PlayerSkills":{"LPS_PILOTING":7,"LPP_SPACE":29267},
         "Affiliations":[{"Tag":"Steel","Standing":-100,"Title":-2}],"DailyAffiliationCetus":0,"DailyFocus":0}
        """);
    Equal(1L, s.Progress!.Nodes["Node"]); Equal(1, s.Progress.Intrinsics.Count);
    Equal(-2L, s.Standing!.Syndicates["Steel"].Title); Equal(0L, s.Standing.Daily["DailyAffiliationCetus"]);
});
Test("Unknown auth fields are excluded from normalized exports", () =>
{
    var json = JsonSerializer.Serialize(Parse("""{"MiscItems":[],"nonce":"secret-value","token":"secret-value"}"""), SnapshotJson.Options);
    Equal(false, json.Contains("secret-value"));
});
Test("Complete snapshot survives restart and partial observations", () =>
{
    var file = Path.Combine(temp, Guid.NewGuid() + ".sqlite");
    var store = new SnapshotStore(file);
    Equal(true, store.Commit(Snapshot(100)).Promoted);
    Equal(false, store.Commit(Snapshot(200, 0, false)).Promoted);
    Equal(5L, new SnapshotStore(file).Current("test")!.Counts["/P"]);
    Equal(2, store.History("test").Count); Equal("ok", store.IntegrityCheck());
});
Test("Older, equal and missing source timestamps cannot erase current inventory", () =>
{
    var store = Store(); store.Commit(Snapshot(200));
    Equal(false, store.Commit(Snapshot(100, 0)).Promoted);
    Equal(false, store.Commit(Snapshot(200, 0)).Promoted);
    Equal(false, store.Commit(Parse("""{"MiscItems":[]}""", complete: true)).Promoted);
    Equal(5L, store.Current("test")!.Counts["/P"]);
    Equal(true, store.Commit(Snapshot(300, 0)).Promoted);
    Equal(0L, store.Current("test")!.Counts["/P"]);
});
Test("Partial-only account has no fabricated confirmed inventory", () =>
{
    var store = Store(); store.Commit(Snapshot(100, complete: false));
    Equal(null, store.Current("test")); Equal(1, store.History("test").Count);
});
Test("Accounts remain isolated even with interleaved writes", () =>
{
    var store = Store(); store.Commit(Snapshot(100)); store.Commit(Snapshot(200, 99, account: "second"));
    Equal(5L, store.Current("test")!.Counts["/P"]); Equal(99L, store.Current("second")!.Counts["/P"]);
    Equal(1, store.History("test").Count); Equal(null, store.Current("missing"));
});
Test("Caller mutation cannot change persisted state", () =>
{
    var store = Store(); var s = Snapshot(100); store.Commit(s); s.Counts.Clear();
    var current = store.Current("test")!; current.Counts.Clear();
    Equal(5L, store.Current("test")!.Counts["/P"]);
});
Test("Cancelled commit leaves neither history nor current pointer", () =>
{
    var store = Store();
    Throws<OperationCanceledException>(() => store.Commit(Snapshot(100), new CancellationToken(true)));
    Equal(0, store.History("test").Count); Equal(null, store.Current("test"));
});
Test("SQLite write failure rolls back history and current pointer together", () =>
{
    var file = Path.Combine(temp, Guid.NewGuid() + ".sqlite"); var store = new SnapshotStore(file);
    store.Commit(Snapshot(100));
    using (var db = new SqliteConnection($"Data Source={file};Pooling=False"))
    {
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TRIGGER fail_update BEFORE UPDATE ON current_snapshots BEGIN SELECT RAISE(ABORT,'simulated failure'); END;";
        cmd.ExecuteNonQuery();
    }
    Throws<SqliteException>(() => store.Commit(Snapshot(200, 0)));
    Equal(1, store.History("test").Count); Equal(5L, store.Current("test")!.Counts["/P"]);
});
Test("Concurrent writers cannot roll back to an older sync", () =>
{
    var store = Store();
    Parallel.For(1, 15, i => store.Commit(Snapshot(i, i)));
    Equal<double?>(14, store.Current("test")!.SourceTimestamp); Equal(14, store.History("test").Count);
});
Test("Backup is readable and existing output cannot be overwritten", () =>
{
    var store = Store(); store.Commit(Snapshot(100)); var backup = Path.Combine(temp, Guid.NewGuid() + ".sqlite");
    store.Backup(backup); Equal("ok", new SnapshotStore(backup).IntegrityCheck());
    Equal(5L, new SnapshotStore(backup).Current("test")!.Counts["/P"]);
    Throws<IOException>(() => store.Backup(backup));
});
Test("Newer database schema fails closed", () =>
{
    var file = Path.Combine(temp, Guid.NewGuid() + ".sqlite");
    using (var db = new SqliteConnection($"Data Source={file};Pooling=False"))
    {
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; cmd.ExecuteNonQuery();
    }
    Throws<InventoryException>(() => new SnapshotStore(file));
});

ServiceTests.Register(Test);
IntegrationTests.Register(Test);
if (args.Contains("--ocr"))
{
    Console.WriteLine("OCR executable: " + WarframeCompanion.Services.ScreenCapture.Which("tesseract"));
    IntegrationTests.RegisterOcr(Test);
}
string? Option(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
if (Option("--reward-image") is { } rewardImage) IntegrationTests.RegisterRewardImage(Test, rewardImage, Option("--cache") ?? throw new ArgumentException("--cache is required"), Option("--expected-rewards")?.Split('|'));
if (Option("--live-memory-directory") is { } liveDirectory) IntegrationTests.RegisterLiveMemory(Test, liveDirectory);
if (Option("--filter") is { } filter) tests.RemoveAll(test => !test.Item1.Contains(filter, StringComparison.OrdinalIgnoreCase));
if (tests.Count == 0) throw new ArgumentException("No tests matched the filter.");
var failures = 0;
try
{
    foreach (var (name, test) in tests)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
    }
}
finally { Directory.Delete(temp, recursive: true); }
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;
