using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public sealed record CachedDocument(JsonNode Data, bool Stale, string? Warning);

public sealed class CachedJson(string directory, bool offline = false) : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim slots = new(4);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> keys = new();
    public async Task<byte[]> Blob(string url, TimeSpan maxAge, bool force = false, CancellationToken cancellation = default)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".bin"; var path = Path.Combine(directory, key); var gate = keys.GetOrAdd(key, _ => new(1));
        await gate.WaitAsync(cancellation);
        try
        {
            var old = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellation) : null;
            if (old != null && (!force && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge || offline)) return old;
            if (offline) throw new IOException("No offline copy is available."); await slots.WaitAsync(cancellation);
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellation); using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0) { if (output.Length + count > 32 * 1024 * 1024) throw new IOException("Download exceeds limit."); output.Write(buffer, 0, count); }
                var bytes = output.ToArray(); if (bytes.Length == 0) throw new IOException("Empty download."); AtomicWrite(path, bytes); return bytes;
            }
            catch (Exception e) when (old != null && !cancellation.IsCancellationRequested && e is HttpRequestException or IOException or TaskCanceledException) { return old; }
            finally { slots.Release(); }
        }
        finally { gate.Release(); }
    }
    public async Task<CachedDocument> Get(string url, TimeSpan maxAge, bool force = false, CancellationToken cancellation = default)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var gate = keys.GetOrAdd(key, _ => new(1));
        await gate.WaitAsync(cancellation);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, key + ".json"); JsonNode? cached = null;
            if (File.Exists(path))
            {
                try { cached = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellation)); }
                catch (JsonException) { /* A corrupt cache must be fetched without a validator. */ }
            }
            if (cached != null && !force && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge) return new(cached, false, null);
            if (offline)
                return cached != null ? new(cached, true, "Offline: using cached data.") : throw new IOException("No offline cache is available for this data source.");
            await slots.WaitAsync(cancellation);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("WarframeCompanion/0.1 (+https://github.com/Lenalein2001/Warframe-App)");
                if (cached != null && File.Exists(path + ".etag")) request.Headers.TryAddWithoutValidation("If-None-Match", await File.ReadAllTextAsync(path + ".etag", cancellation));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if (response.StatusCode == HttpStatusCode.NotModified && cached != null) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return new(cached, false, null); }
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
                using var data = new MemoryStream(); var buffer = new byte[65536]; int count;
                while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
                {
                    if (data.Length + count > 64 * 1024 * 1024) throw new IOException("Data source exceeds the 64 MiB limit.");
                    data.Write(buffer, 0, count);
                }
                var parsed = JsonNode.Parse(data.ToArray()) ?? throw new JsonException("Empty response");
                if (parsed is not (JsonArray or JsonObject)) throw new JsonException("Expected a document");
                AtomicWrite(path, data.ToArray());
                if (response.Headers.ETag is { } etag) AtomicWrite(path + ".etag", Encoding.UTF8.GetBytes(etag.ToString()));
                else if (File.Exists(path + ".etag")) File.Delete(path + ".etag");
                return new(parsed, false, null);
            }
            catch (Exception e) when (cached != null && !cancellation.IsCancellationRequested && e is HttpRequestException or IOException or JsonException or TaskCanceledException)
            { return new(cached, true, "Refresh failed: using the last valid cached data."); }
            finally { slots.Release(); }
        }
        finally { gate.Release(); }
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { client.Dispose(); slots.Dispose(); foreach (var gate in keys.Values) gate.Dispose(); }
}
