using System.Text;
using System.Text.Json.Nodes;
using WarframeCompanion.Services;

namespace WarframeCompanion.Host;

public sealed class ProfileInUseException() : IOException("This profile is already open in another companion process.");

public static class ProfileInstance
{
    public static FileStream Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 11) { throw new ProfileInUseException(); }
        // Leave the empty file in place: deleting it could let another process lock a different inode.
    }

    public static async Task<bool> TryShow(string directory, int? requestedPort = null)
    {
        try
        {
            var settings = File.Exists(Path.Combine(directory, "state.sqlite"))
                ? new StateStore(Path.Combine(directory, "state.sqlite")).Read<JsonObject>("settings") : null;
            var port = requestedPort ?? (int)J.N(settings, "port", 8642);
            if (port is < 1024 or > 65535) return false;
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", LocalApiCredential.Read(directory, port));
            using var response = await client.PostAsync($"http://127.0.0.1:{port}/api/window/show", new StringContent("{}", Encoding.UTF8, "application/json"));
            return response.IsSuccessStatusCode && JsonNode.Parse(await response.Content.ReadAsStringAsync())?["shown"]?.GetValue<bool>() == true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException) { return false; }
    }
}
