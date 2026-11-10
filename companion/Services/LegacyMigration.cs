using System.Text.Json;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;

namespace WarframeCompanion.Services;

public static class LegacyMigration
{
    private static JsonNode? Read(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    private static JsonNode? Camel(JsonNode? value)
    {
        if (value is JsonArray array) return new JsonArray(array.Select(Camel).ToArray());
        if (value is not JsonObject obj) return value?.DeepClone();
        var next = new JsonObject(); foreach (var (key, child) in obj) { var mapped = key switch { "mastery_rank" => "masteryRank", "daily_focus" => "dailyFocus", _ => key }; next[mapped] = Camel(child); } return next;
    }
    public static (RegistryState Registry, JsonObject Settings)? Load(string data, string config, SnapshotStore snapshots)
    {
        var raw = Read(Path.Combine(data, "accounts.json")); var oldSettings = Read(Path.Combine(config, "settings.json"));
        if (raw == null && oldSettings == null && !File.Exists(Path.Combine(data, "profile.json"))) return null;
        var settings = Settings.Defaults();
        if (oldSettings is JsonObject fields) foreach (var field in fields) { try { settings = Settings.Patch(settings, new JsonObject { [field.Key] = field.Value?.DeepClone() }); } catch (InventoryException) { } }
        var registry = new RegistryState { Active = J.S(raw, "active") };
        var accounts = J.A(raw, "accounts").ToArray();
        if (accounts.Length == 0) accounts = [J.Node(new { key = "legacy", name = "Account 1", id = J.S(oldSettings, "account_id") })];
        foreach (var old in accounts)
        {
            var key = J.S(old, "key") ?? "legacy"; InventoryParser.ValidateAccount(key);
            var folder = raw == null ? data : Path.Combine(data, "accounts", key);
            var account = new AccountState { Key = key, Id = J.S(old, "id"), Name = J.S(old, "name"), Source = J.S(old, "source") ?? "saved", Seen = J.D(old?["seen"]) };
            if (old?["badge"] is JsonArray badge && badge.Count == 2) { account.Badge = J.D(badge[0]) is { } low ? (long)low : null; account.BadgeMax = J.D(badge[1]) is { } high ? (long)high : null; }
            if (old?["duviri"] is { } duviri) account.Duviri = J.Node(new { rolled = J.D(duviri["rolled"]), offers = duviri["picks"] });
            if (account.Id != null) InventoryParser.ValidateAccount(account.Id);
            if (Read(Path.Combine(folder, "profile.json")) is { } profile) account.Profile = Camel(profile)!.Deserialize<Profile>(J.Options);
            if (Read(Path.Combine(folder, "manual.json")) is { } manual)
            {
                foreach (var (uid, count) in J.O(manual["counts"])) if (count is JsonArray pair && pair.Count == 2) account.Manual.Counts[uid] = new(J.N(pair[0]), J.D(pair[1]) ?? 0);
                foreach (var (uid, time) in J.O(manual["mastered"])) account.Manual.Mastered[uid] = J.D(time) ?? 0;
                account.Manual.Pinned = J.A(manual, "pinned").Select(J.S).OfType<string>().ToList();
                account.Manual.Tasks = J.A(manual, "tasks").Select(t => t!.Deserialize<FarmingTask>(J.Options)!).ToList();
                foreach (var goal in J.A(manual, "goals").Select(J.S).OfType<string>()) if (!account.Manual.Tasks.Any(t => t.Uid == goal)) account.Manual.Tasks.Add(new(goal, null, J.Now));
            }
            if (Read(Path.Combine(folder, "inventory.json")) is { } inventory)
            {
                T Get<T>(string name, T fallback) => Camel(inventory[name]) is { } node ? node.Deserialize<T>(J.Options) ?? fallback : fallback;
                var snapshot = new InventorySnapshot(1, account.Id ?? key, false, "Migrated Rust snapshot", DateTimeOffset.FromUnixTimeMilliseconds((long)((J.D(inventory["imported"]) ?? J.Now) * 1000)), J.D(inventory["synced_at"]), true,
                    ["Imported from the existing application's account folder."], Get<Dictionary<string, long>>("counts", []), Get<Dictionary<string, Equipment>>("equipment", []), Get<Dictionary<string, long>>("xp", []), Get<Dictionary<string, List<long>>?>("upgrades", null),
                    J.A(inventory, "foundry").Where(f => J.S(f, "recipe") != null).Select(f => new FoundryJob(J.S(f, "recipe")!, J.D(f?["done"]))).ToArray(), Get("currencies", new Currencies(null, null, null, null)), J.N(inventory, "mods"), J.D(inventory["mastery_rank"]) is { } rank ? (long)rank : null, Get<Progress?>("progress", null), Get<Standing?>("standing", null));
                if (account.Profile == null || SameOwner(snapshot.Xp, account.Profile.Xp) != false) snapshots.Commit(snapshot);
            }
            registry.Accounts.Add(account);
        }
        if (!registry.Accounts.Any(a => a.Key == registry.Active)) registry.Active = registry.Accounts.FirstOrDefault()?.Key;
        return (registry, settings);
    }
    public static bool? SameOwner(Dictionary<string, long> a, Dictionary<string, long> b)
    {
        var shared = a.Where(p => p.Value > 0 && b.GetValueOrDefault(p.Key) > 0).ToArray(); return shared.Length < 10 ? null : shared.Count(p => b[p.Key] == p.Value) * 4 >= shared.Length;
    }
    public static (string Data, string Config) DefaultPaths()
    {
        if (OperatingSystem.IsWindows()) return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "warframeproject"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "warframeproject"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Xdg(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) is { } value && Path.IsPathFullyQualified(value) ? value : Path.Combine(home, fallback);
        return (Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "warframeproject"), Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "warframeproject"));
    }
}
