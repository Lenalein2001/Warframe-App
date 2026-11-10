using System.Globalization;
using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed partial class CompanionRuntime
{
    private readonly HashSet<string> notified = [];
    public event Action<string, string>? NotificationRequested;
    private void Notify(string key, string title, string body)
    {
        lock (notified) if (NotificationRequested == null || !notified.Add(key)) return;
        NotificationRequested.Invoke(title, body);
    }
    private void NotificationTick()
    {
        var config = GetSettings(); var view = View();
        if (J.B(config["notify_fissures"]))
        {
            var watch = J.A(config, "fissure_watch").Select(J.S).OfType<string>().ToHashSet();
            foreach (var fissure in J.A(World.Fissures(view, config["fissure_watch"]), "fissures"))
                if (watch.Contains(J.S(fissure, "watch") ?? "") && J.S(fissure, "id") is { } id)
                    Notify("fissure:" + J.S(config, "platform") + ":" + id, J.S(fissure, "tier") + " " + J.S(fissure, "mission") + " fissure", J.S(fissure, "node") + " · " + J.S(fissure, "enemy"));
        }
        if (J.B(config["notify_baro"]))
        {
            var baro = World.Summary(Catalog)["baro"]; if (J.B(baro?["active"])) Notify("baro:" + J.S(baro, "activation"), "Baro Ki'Teer has arrived", J.S(baro, "location") ?? "A relay");
        }
        if (J.B(config["notify_foundry"]))
        {
            lock (sync) foreach (var account in registry.Accounts) foreach (var build in snapshots.Current(account.Id ?? account.Key)?.Foundry ?? [])
                if (build.Done <= J.Now) Notify("build:" + account.Key + ":" + build.Recipe + ":" + build.Done?.ToString(CultureInfo.InvariantCulture), "Foundry", Catalog.NameOf(build.Recipe) + (registry.Accounts.Count > 1 ? " (" + account.Name + ")" : "") + " is ready to claim");
        }
    }
}
