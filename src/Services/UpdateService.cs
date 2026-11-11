using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed class UpdateService(CachedJson cache)
{
    public const string Version = "0.1.1";
    public JsonNode? Available { get; private set; }
    public static JsonNode? NewerRelease(JsonNode release, string current)
    {
        static Version? Parse(string value) => System.Version.TryParse(value.Trim().TrimStart('v', 'V').Split('-', '+')[0], out var parsed) ? parsed : null;
        var tag = J.S(release, "tag_name") ?? "";
        if (J.B(release["draft"]) || J.B(release["prerelease"]) || Parse(tag) is not { } version || Parse(current) is not { } running || version <= running) return null;
        // The fork also contains historical Rust releases. Only offer a release with this application's assets.
        if (!J.A(release, "assets").Any(a => J.S(a, "name")?.StartsWith("WarframeCompanion-", StringComparison.OrdinalIgnoreCase) == true)) return null;
        var url = J.S(release, "html_url");
        if (url?.StartsWith("https://github.com/Lenalein2001/Warframe-App/releases/", StringComparison.OrdinalIgnoreCase) != true) return null;
        return J.Node(new { version = tag.TrimStart('v', 'V'), current, name = J.S(release, "name"), url, notes = J.S(release, "body"), published = J.S(release, "published_at") });
    }
    public async Task Check(CancellationToken cancellation)
    {
        try
        {
            var releases = await cache.Get("https://api.github.com/repos/Lenalein2001/Warframe-App/releases?per_page=20", TimeSpan.FromHours(1), cancellation: cancellation);
            Available = J.A(releases.Data).Select(r => NewerRelease(r, Version)).FirstOrDefault(r => r != null);
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException) { }
    }
}
