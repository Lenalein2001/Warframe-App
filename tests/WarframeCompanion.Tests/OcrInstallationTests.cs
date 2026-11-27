using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WarframeCompanion.Services;

internal static class OcrInstallationTests
{
    internal static void RegisterPackage(Action<string, Action> test, string archive)
    {
        test("Optional OCR release archive installs and reads text using its own English model", () => Run(async root =>
        {
            using var install = new OcrInstallation(root, OcrInstallation.LoadPackage(), new Download(File.ReadAllBytes(archive)), () => null);
            await install.Install(default);
            using var bitmap = new SkiaSharp.SKBitmap(1000, 100, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
            using var canvas = new SkiaSharp.SKCanvas(bitmap); canvas.Clear(SkiaSharp.SKColors.Black);
            using var fontStream = typeof(OcrInstallationTests).Assembly.GetManifestResourceStream("OcrTestFont.ttf")!;
            using var typeface = SkiaSharp.SKTypeface.FromStream(fontStream);
            using var font = new SkiaSharp.SKFont(typeface, 38);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, IsAntialias = true };
            canvas.DrawText("Ash Prime Systems Blueprint", 20, 60, SkiaSharp.SKTextAlign.Left, font, paint);
            var text = await ScreenCapture.Text(bitmap, new(0, 0, 1000, 100), 6, 0, default, install.Executable);
            Check(OcrReader.Match(text, ["Ash Prime Systems Blueprint"]).Name == "Ash Prime Systems Blueprint", "Downloaded OCR/model did not read the fixture");
        }));
    }
    internal static void Register(Action<string, Action> test)
    {
        if (!OperatingSystem.IsWindows()) return;
        test("Optional OCR installs verified files, repairs missing files and reuses installed files", () => Run(async root =>
        {
            var (package, bytes) = Fixture();
            var handler = new Download(bytes);
            using var install = new OcrInstallation(root, package, handler, () => null);
            Check(install.Executable == null, "Unexpected OCR");
            await install.Install(default);
            Check(install.Executable != null, "OCR not installed");
            await install.Install(default);
            Check(handler.Calls == 1, "Unnecessary second download");
            File.Delete(Path.Combine(root, "components", "ocr", package.Version, "tessdata", "eng.traineddata"));
            await install.Install(default);
            Check(handler.Calls == 2 && install.Executable != null, "Partial installation not repaired");
            Check(!Directory.GetDirectories(Path.Combine(root, "components", "ocr"), ".download-*").Any(), "Temporary files retained");
        }));
        test("Optional OCR rejects altered downloads and unsafe archive entries", () => Run(async root =>
        {
            var (package, bytes) = Fixture();
            using (var install = new OcrInstallation(root, package with { Sha256 = new string('0', 64) }, new Download(bytes), () => null))
                await Reject(() => install.Install(default));
            (package, bytes) = Fixture("../escape.exe");
            using (var install = new OcrInstallation(root, package, new Download(bytes), () => null))
                await Reject(() => install.Install(default));
            Check(!File.Exists(Path.Combine(root, "components", "ocr", "escape.exe")), "Archive escaped staging");
            Check(!Directory.Exists(Path.Combine(root, "components", "ocr", package.Version)), "Failed package was activated");
        }));
        test("Optional OCR rejects HTTP redirects and cancels without installing", () => Run(async root =>
        {
            var (package, bytes) = Fixture();
            using (var install = new OcrInstallation(root, package, new Download(bytes, "http://github.com/unsafe"), () => null))
                await Reject(() => install.Install(default));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            using (var install = new OcrInstallation(root, package, new Download(bytes), () => null))
            {
                try { await install.Install(canceled.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { }
            }
            Check(!Directory.Exists(Path.Combine(root, "components", "ocr", package.Version)), "Canceled package was activated");
        }));
        test("Optional OCR serializes installs and permits retry after cancellation", () => Run(async root =>
        {
            var (package, bytes) = Fixture();
            var handler = new PausedDownload(bytes);
            using var install = new OcrInstallation(root, package, handler, () => null);
            using var cancellation = new CancellationTokenSource();
            var pending = install.Install(cancellation.Token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await install.Install(default); throw new Exception("Concurrent install accepted"); }
            catch (WarframeCompanion.Core.InventoryException) { }
            cancellation.Cancel();
            try { await pending; throw new Exception("Canceled download completed"); }
            catch (OperationCanceledException) { }
            handler.Pause = false;
            await install.Install(default);
            Check(install.Executable != null, "Retry after cancellation failed");
        }));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (InvalidDataException) { return; }
        throw new Exception("Unsafe download accepted");
    }
    private static void Run(Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "wfc-ocr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root).GetAwaiter().GetResult(); } finally { Directory.Delete(root, true); }
    }
    private static (OcrPackage, byte[]) Fixture(string executable = "tesseract.exe")
    {
        using var output = new MemoryStream();
        var files = new Dictionary<string, OcrFile>();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var name in new[] { executable, "tessdata/eng.traineddata" })
            {
                var bytes = Encoding.UTF8.GetBytes("fixture; never executed");
                using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes);
                files[name] = new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            }
        var data = output.ToArray();
        return (new("test", "https://github.com/Lenalein2001/Warframe-App/releases/download/ocr-v1/test.zip", data.Length, Convert.ToHexString(SHA256.HashData(data)), files), data);
    }
    private sealed class Download(byte[] bytes, string? redirect = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(redirect == null ? HttpStatusCode.OK : HttpStatusCode.Redirect) { Content = new ByteArrayContent(bytes) };
            if (redirect != null) response.Headers.Location = new Uri(redirect);
            return Task.FromResult(response);
        }
    }
    private sealed class PausedDownload(byte[] bytes) : HttpMessageHandler
    {
        public bool Pause = true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Pause) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
