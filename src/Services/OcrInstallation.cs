using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

internal sealed record OcrFile(long Bytes, string Sha256);
internal sealed record OcrPackage(string Version, string Url, long Bytes, string Sha256, Dictionary<string, OcrFile> Files);

// This descriptor is shipped with the app, never supplied by the local API or a remote manifest.
// Downloads are opt-in, bounded, hash-verified, and installed atomically in the selected profile.
public sealed class OcrInstallation : IDisposable
{
    private readonly OcrPackage package;
    private readonly string root;
    private readonly HttpClient client;
    private readonly Func<string?> discover;
    private readonly SemaphoreSlim gate = new(1);
    private volatile string phase = "idle";
    private long received;
    public OcrInstallation(string profile) : this(profile, LoadPackage(), null) { }
    internal OcrInstallation(string profile, OcrPackage package, HttpMessageHandler? handler, Func<string?>? discover = null)
    {
        this.package = package;
        this.discover = discover ?? (() => ScreenCapture.Which("tesseract"));
        root = Path.Combine(profile, "components", "ocr");
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WarframeCompanion/" + UpdateService.Version);
    }
    internal static OcrPackage LoadPackage()
    {
        using var stream = typeof(OcrInstallation).Assembly.GetManifestResourceStream("ocr-package.json")!;
        return JsonSerializer.Deserialize<OcrPackage>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    private string Destination => Path.Combine(root, package.Version);
    public string? Executable
    {
        get
        {
            var executable = Path.Combine(Destination, "tesseract.exe");
            if (OperatingSystem.IsWindows() && package.Files.All(f => File.Exists(Path.Combine(Destination, f.Key)) && new FileInfo(Path.Combine(Destination, f.Key)).Length == f.Value.Bytes)) return executable;
            return discover();
        }
    }
    public object Status() => new { installed = Executable != null, supported = OperatingSystem.IsWindows(), phase,
        received = Interlocked.Read(ref received), bytes = package.Bytes };

    private static bool Allowed(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    public async Task Install(CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new InventoryException("Install Tesseract English OCR using your Linux software manager.");
        if (!await gate.WaitAsync(0, cancellation)) throw new InventoryException("OCR installation is already in progress.");
        string? stage = null;
        try
        {
            if (Executable != null) return;
            if (package.Bytes <= 0 || package.Bytes > 20 * 1024 * 1024 || package.Files.Values.Sum(f => f.Bytes) > 32 * 1024 * 1024)
                throw new InvalidDataException("Invalid OCR package limits.");
            Directory.CreateDirectory(root);
            // A second process must not replace a component while another is installing it.
            using var lease = new FileStream(Path.Combine(root, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stage = Path.Combine(root, ".download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            phase = "downloading"; Interlocked.Exchange(ref received, 0);
            var uri = new Uri(package.Url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            HttpResponseMessage? response = null;
            try
            {
                for (var redirects = 0; redirects <= 5; redirects++)
                {
                    if (!Allowed(uri)) throw new InvalidDataException("Untrusted OCR download location.");
                    response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                    { uri = new Uri(uri, location); response.Dispose(); response = null; continue; }
                    break;
                }
                if (response == null) throw new IOException("Too many OCR download redirects.");
                if (response.StatusCode == HttpStatusCode.NotFound) throw new InventoryException("The OCR download is not available for this build yet. Try again after the release is published.");
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } size && size != package.Bytes) throw new InvalidDataException("Unexpected OCR download size.");
                var zipPath = Path.Combine(stage, "package.zip");
                await using (var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                {
                    var buffer = new byte[65536];
                    int count;
                    while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                    {
                        if (Interlocked.Add(ref received, count) > package.Bytes) throw new InvalidDataException("OCR download exceeds its expected size.");
                        await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    }
                }
                phase = "verifying";
                await Verify(zipPath, new(package.Bytes, package.Sha256), timeout.Token);
                var unpack = Path.Combine(stage, "unpacked");
                Directory.CreateDirectory(unpack);
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    if (archive.Entries.Count != package.Files.Count) throw new InvalidDataException("Unexpected OCR archive contents.");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in archive.Entries)
                    {
                        var name = entry.FullName;
                        if (!package.Files.TryGetValue(name, out var expected) || !seen.Add(name) ||
                            name.Contains('\\') || name.Contains(':') || name.Split('/').Any(p => p is "" or "." or "..") ||
                            entry.Length != expected.Bytes || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                            throw new InvalidDataException("Unexpected OCR archive entry.");
                        var target = Path.Combine(unpack, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target);
                        await Verify(target, expected, timeout.Token);
                    }
                }
                timeout.Token.ThrowIfCancellationRequested();
                phase = "installing";
                // Never merge partial downloads with a working installation.
                var previous = Path.Combine(stage, "previous");
                if (Directory.Exists(Destination)) Directory.Move(Destination, previous);
                try { Directory.Move(unpack, Destination); }
                catch { if (Directory.Exists(previous)) Directory.Move(previous, Destination); throw; }
            }
            finally { response?.Dispose(); }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new InventoryException("OCR download timed out. Please retry."); }
        finally
        {
            try { if (stage != null && Directory.Exists(stage)) Directory.Delete(stage, true); }
            finally { phase = "idle"; gate.Release(); }
        }
    }
    private static async Task Verify(string path, OcrFile expected, CancellationToken cancellation)
    {
        await using var file = File.OpenRead(path);
        if (file.Length != expected.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OCR download failed integrity verification.");
    }
    public void Dispose() => client.Dispose();
}
