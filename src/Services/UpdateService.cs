using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed class UpdateService(CachedJson cache)
{
    public const string Version = "0.1.1";
    public JsonNode? Available { get; private set; }
    public static JsonNode? NewerRelease(JsonNode release, string current, string? platform = null)
    {
        static Version? Parse(string value) => System.Version.TryParse(value.Trim().TrimStart('v', 'V').Split('-', '+')[0], out var parsed) ? parsed : null;
        var tag = J.S(release, "tag_name") ?? "";
        if (J.B(release["draft"]) || J.B(release["prerelease"]) || Parse(tag) is not { } version || Parse(current) is not { } running || version <= running) return null;
        platform ??= OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unsupported";
        // The fork also contains historical Rust releases. Only offer a release with this application's assets.
        var prefix = "WarframeCompanion-" + platform;
        string[] packages = [prefix + ".zip", prefix + ".deb", prefix + "-portable.tar.gz", prefix + "-Setup.exe",
            prefix + "-self-contained.zip", prefix + "-framework-dependent.zip"];
        if (!J.A(release, "assets").Any(a => packages.Contains(J.S(a, "name"), StringComparer.OrdinalIgnoreCase))) return null;
        var url = J.S(release, "html_url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme != "https" ||
            !link.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !link.IsDefaultPort ||
            link.UserInfo.Length != 0 || !link.AbsolutePath.StartsWith("/Lenalein2001/Warframe-App/releases/tag/", StringComparison.OrdinalIgnoreCase)) return null;
        return J.Node(new { version = tag.TrimStart('v', 'V'), current, name = J.S(release, "name"), url, notes = J.S(release, "body"), published = J.S(release, "published_at") });
    }
    public static JsonNode? LatestRelease(IEnumerable<JsonNode> releases, string current, string? platform = null) =>
        releases.Select(r => NewerRelease(r, current, platform)).Where(r => r != null)
            .OrderByDescending(r => System.Version.Parse(J.S(r!, "version")!.Split('-', '+')[0])).FirstOrDefault();

    public async Task Check(CancellationToken cancellation)
    {
        try
        {
            var releases = await cache.Get("https://api.github.com/repos/Lenalein2001/Warframe-App/releases?per_page=20", TimeSpan.FromHours(1), cancellation: cancellation);
            Available = LatestRelease(J.A(releases.Data), Version);
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException) { }
    }
}
