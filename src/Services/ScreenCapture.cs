using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed record CapturedScreen(SKBitmap Bitmap, int X, int Y, double CoordinateScale = 1) : IDisposable { public void Dispose() => Bitmap.Dispose(); }
public static class ScreenCapture
{
    public static CapturedScreen ReadSaved(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024) throw new InventoryException("Saved screenshot exceeds 64 MiB.");
        using var codec = SKCodec.Create(stream);
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 40_000_000)
            throw new InventoryException("Saved screenshot is invalid or too large.");
        return new(SKBitmap.Decode(codec) ?? throw new InventoryException("Saved screenshot could not be decoded."), 0, 0);
    }
    public static string? Which(string name)
    {
        var executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "tools", name), Path.Combine(AppContext.BaseDirectory, "tools"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR") }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        { var path = Path.Combine(dir, executable); if (File.Exists(path)) return path; }
        return null;
    }
    public static string? ScreenshotTool(string setting) => OperatingSystem.IsWindows() ? "Windows GDI" : (setting == "auto" ? new[] { "grim", "spectacle", "gnome-screenshot", "maim", "scrot", "import" } : [setting]).FirstOrDefault(t => Which(t) != null);
    public static async Task<string> Run(string executable, IEnumerable<string> arguments, CancellationToken cancellation, int seconds = 20)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start capture/OCR tool."); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await errors; if (process.ExitCode != 0) throw new InventoryException("Capture/OCR tool failed. Check that the selected tool and English OCR data are installed."); return await output; }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
    public static async Task<CapturedScreen> Capture(string setting, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows()) return Windows();
        var tool = ScreenshotTool(setting) ?? throw new InventoryException("Install a screenshot tool such as grim or scrot."); var path = Path.Combine(Path.GetTempPath(), "warframe-frame-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var bounds = tool == "grim" ? await LinuxCompositor.GameBounds(cancellation) : null;
            string[] args = tool switch { "grim" => [path], "spectacle" => ["-b", "-n", "-o", path], "gnome-screenshot" => ["-f", path], "maim" => [path], "scrot" => ["-o", path], "import" => ["-window", "root", path], _ => throw new InventoryException("Unknown screenshot tool.") };
            if (tool == "grim") args = bounds is { } r ? ["-l", "0", "-g", $"{r.X},{r.Y} {r.Width}x{r.Height}", path] : ["-l", "0", path];
            await Run(Which(tool)!, args, cancellation); using var codec = SKCodec.Create(path);
            if (codec == null || (long)codec.Info.Width * codec.Info.Height > 40_000_000) throw new InventoryException("Screenshot is invalid or too large.");
            return new(SKBitmap.Decode(codec) ?? throw new InventoryException("Screenshot could not be read."), bounds?.X ?? 0, bounds?.Y ?? 0, bounds == null ? 1 : codec.Info.Width / (double)bounds.Width);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    public static async Task<string> Text(SKBitmap bitmap, SKRectI rectangle, int mode, int variant, CancellationToken cancellation)
    {
        var tesseract = Which("tesseract") ?? throw new InventoryException("Tesseract is not installed. Install English Tesseract OCR or place it in tools/tesseract.");
        rectangle.Intersect(new SKRectI(0, 0, bitmap.Width, bitmap.Height)); if (rectangle.Width < 2 || rectangle.Height < 2) throw new InventoryException("OCR crop lies outside the screenshot.");
        using var crop = new SKBitmap(); if (!bitmap.ExtractSubset(crop, rectangle)) throw new InventoryException("OCR crop failed.");
        using var prepared = crop.Copy(SKColorType.Bgra8888); NativeEngine.Prepare(prepared, variant);
        using var scaled = prepared.Resize(new SKImageInfo(crop.Width * 2, crop.Height * 2), new SKSamplingOptions(SKCubicResampler.CatmullRom));
        var path = Path.Combine(Path.GetTempPath(), "warframe-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try { using (var image = SKImage.FromBitmap(scaled)) using (var data = image.Encode(SKEncodedImageFormat.Png, 100)) await File.WriteAllBytesAsync(path, data.ToArray(), cancellation);
            return await Run(tesseract, [path, "stdout", "-l", "eng", "--psm", mode.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellation); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private static CapturedScreen Windows()
    {
        var x = GetSystemMetrics(76); var y = GetSystemMetrics(77); var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (MemoryProvider.GamePid() is { } pid)
        {
            using var game = Process.GetProcessById(pid); var window = game.MainWindowHandle;
            if (window != 0 && GetClientRect(window, out var rect) && rect.Right > 0 && rect.Bottom > 0) { var point = new Point(); ClientToScreen(window, ref point); x = point.X; y = point.Y; width = rect.Right; height = rect.Bottom; }
        }
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) throw new InventoryException("No supported screen bounds.");
        var screen = GetDC(0); var dc = CreateCompatibleDC(screen); var bitmap = CreateCompatibleBitmap(screen, width, height); var old = SelectObject(dc, bitmap);
        try
        {
            if (!BitBlt(dc, 0, 0, width, height, screen, x, y, 0x40CC0020)) throw new InventoryException("Windows could not capture the game screen.");
            SelectObject(dc, old); var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 }; var bytes = new byte[width * height * 4];
            if (GetDIBits(dc, bitmap, 0, (uint)height, bytes, ref info, 0) == 0) throw new InventoryException("Windows could not read screenshot pixels.");
            var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque); Marshal.Copy(bytes, 0, result.GetPixels(), bytes.Length); return new(result, x, y);
        }
        finally { SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(0, screen); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bytes, ref BitmapInfo info, uint usage);
}
