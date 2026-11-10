using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed class ArtService(string directory, bool offline) : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim gate = new(4);
    private readonly ConcurrentDictionary<string, double> failed = new();
    public static bool Allowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 && !Uri.UnescapeDataString(url).Contains("..", StringComparison.Ordinal) &&
        (u.Host == "cdn.warframestat.us" && u.AbsolutePath.StartsWith("/img/", StringComparison.Ordinal) || u.Host == "warframe.market" && u.AbsolutePath.StartsWith("/static/assets/", StringComparison.Ordinal) || u.Host == "browse.wf" && u.AbsolutePath.StartsWith("/Lotus/Interface/", StringComparison.Ordinal));
    public static bool AllowedRedirect(string url) => Allowed(url) || Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 && u.Host == "raw.githubusercontent.com" && u.AbsolutePath.StartsWith("/wfcd/warframe-items/master/data/img/", StringComparison.OrdinalIgnoreCase) && !Uri.UnescapeDataString(url).Contains("..", StringComparison.Ordinal);
    private async Task<HttpResponseMessage> Download(string url, CancellationToken cancellation)
    {
        for (var hops = 0; hops < 4; hops++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("WarframeCompanion/0.1 (+https://github.com/Lenalein2001/Warframe-App)");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (location == null) throw new IOException("Artwork redirect has no destination.");
            url = new Uri(new Uri(url), location).AbsoluteUri;
            if (!AllowedRedirect(url)) throw new IOException("Artwork redirect is outside trusted sources.");
        }
        throw new IOException("Artwork redirect limit exceeded.");
    }
    public async Task<byte[]> Get(string url, int size, CancellationToken cancellation)
    {
        if (!Allowed(url)) throw new InventoryException("Unknown artwork source.");
        size = Math.Clamp(size, 16, 512); var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + "-" + size + ".png");
        await gate.WaitAsync(cancellation);
        try
        {
            var old = File.Exists(file) ? await File.ReadAllBytesAsync(file, cancellation) : null;
            if (old != null && (offline || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(30))) return old;
            if (offline || failed.TryGetValue(url, out var time) && J.Now - time < 600) return old ?? throw new IOException("Artwork unavailable offline.");
            try
            {
                using var response = await Download(url, cancellation); response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new IOException("Artwork exceeds limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellation); using var bytes = new MemoryStream(); var buffer = new byte[16384];
                int read; while ((read = await stream.ReadAsync(buffer, cancellation)) > 0) { if (bytes.Length + read > 8 * 1024 * 1024) throw new IOException("Artwork exceeds limit."); bytes.Write(buffer, 0, read); }
                using var codec = SKCodec.Create(new SKMemoryStream(bytes.ToArray()));
                if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 16_000_000 || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp)) throw new IOException("Unexpected artwork format.");
                using var bitmap = SKBitmap.Decode(codec); if (bitmap == null) throw new IOException("Artwork could not be decoded.");
                var scale = Math.Min(1, size / (double)Math.Max(bitmap.Width, bitmap.Height));
                using var resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale))), new SKSamplingOptions(SKCubicResampler.Mitchell));
                using var image = SKImage.FromBitmap(resized); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); var result = encoded.ToArray();
                Directory.CreateDirectory(directory); var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temp, result, cancellation); File.Move(temp, file, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
                return result;
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or TaskCanceledException) { failed[url] = J.Now; return old ?? throw new IOException("Artwork unavailable.", e); }
        }
        finally { gate.Release(); }
    }
    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
