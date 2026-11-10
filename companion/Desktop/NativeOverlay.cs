using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Photino.NET;
using WarframeCompanion.Services;

namespace WarframeCompanion.Desktop;

internal sealed class NativeOverlay(PhotinoWindow parent, string url, string directory, Action<string> reportError) : IDisposable
{
    private PhotinoWindow? overlay;
    private CancellationTokenSource? timer;
    private OverlayRequest? current;
    private Rectangle[] currentBoxes = [];
    private int currentLeft, currentTop;
    public void Show(OverlayRequest request) => parent.Invoke(() => { try { ShowOnUi(request); } catch (Exception e) { overlay = null; reportError("Overlay failed: " + e.Message); } });
    private void ShowOnUi(OverlayRequest request)
    {
        var foreground = OperatingSystem.IsWindows() ? GetForegroundWindow() : 0;
        HideOnUi();
        if (OperatingSystem.IsLinux() && request.CoordinateScale != 1) request = request with { Width = (int)Math.Round(request.Width / request.CoordinateScale), Height = (int)Math.Round(request.Height / request.CoordinateScale), Unit = request.Unit / request.CoordinateScale, CoordinateScale = 1 };
        var boxes = OverlayLayout.Boxes(request); var left = boxes.Min(b => b.X); var top = boxes.Min(b => b.Y); var right = boxes.Max(b => b.Right); var bottom = boxes.Max(b => b.Bottom);
        // One narrow window per overlay bounds keeps unrelated desktop areas outside the webview.
        var width = Math.Max(1, right - left); var height = Math.Max(1, bottom - top);
        current = request; currentBoxes = boxes; currentLeft = left; currentTop = top;
        if (OperatingSystem.IsLinux())
        {
            var payload = JsonSerializer.Serialize(new { kind = request.Kind, result = request.Result, unit = request.Unit, boxes = boxes.Select(b => new[] { b.X - left, b.Y - top, b.Width, b.Height }) }, J.Options);
            var code = LinuxShow(url + "/overlay.html?bridge=native", payload, left, top, width, height, request.Seconds, request.Backend == "layer-shell" ? 1 : request.Backend == "x11" ? 2 : 0);
            if (code != 0) reportError(code == -3 ? "This compositor cannot host the selected overlay backend. Use an X11 session or a layer-shell compositor." : "Linux overlay could not be created (" + code + ").");
            return;
        }
        var window = new PhotinoWindow(parent) { LogVerbosity = 0 }.SetTitle("Warframe Companion Overlay").SetChromeless(true).SetTransparent(true).SetTopMost(true).SetResizable(false)
            .SetUseOsDefaultSize(false).SetUseOsDefaultLocation(false).SetSize(new Size(width, height)).SetLocation(new Point(left, top)).SetContextMenuEnabled(false).SetDevToolsEnabled(false)
            .SetTemporaryFilesPath(Path.Combine(directory, "webview-overlay"));
        overlay = window;
        window.RegisterWindowCreatedHandler((_, _) =>
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = window.WindowHandle; var style = GetWindowLongPtrW(handle, -20).ToInt64(); SetWindowLongPtrW(handle, -20, (nint)(style | 0x08000000 | 0x80000 | 0x20 | 0x80));
                // WebView's transparent surface is black in a layered HWND. Key it
                // out so the gap between cards does not obscure the game.
                SetLayeredWindowAttributes(handle, 0, 255, 3);
                SetWindowPos(handle, (nint)(-1), left, top, width, height, 0x10 | 0x40);
            }
        });
        window.RegisterWebMessageReceivedHandler((_, message) =>
        {
            if (message != "ready" || overlay != window) return;
            if (OperatingSystem.IsWindows() && foreground != 0 && GetForegroundWindow() == window.WindowHandle) SetForegroundWindow(foreground);
            window.SendWebMessage(JsonSerializer.Serialize(new { kind = request.Kind, result = request.Result, unit = request.Unit,
                boxes = boxes.Select(b => new[] { b.X - left, b.Y - top, b.Width, b.Height }) }, J.Options));
            timer = new(); _ = Expire(window, Math.Clamp(request.Seconds, 3, 300), timer.Token);
        });
        window.Load(url + "/overlay.html?bridge=photino"); window.WaitForClose();
    }
    private async Task Expire(PhotinoWindow window, int seconds, CancellationToken cancellation)
    { try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancellation); parent.Invoke(() => { if (overlay == window) HideOnUi(); }); } catch (OperationCanceledException) { } }
    public void Hide() => parent.Invoke(HideOnUi);
    public void Update(string kind, System.Text.Json.Nodes.JsonNode result) => parent.Invoke(() =>
    {
        if (current?.Kind != kind) return;
        var payload = JsonSerializer.Serialize(new { kind, result, unit = current.Unit, boxes = currentBoxes.Select(b => new[] { b.X - currentLeft, b.Y - currentTop, b.Width, b.Height }) }, J.Options);
        if (OperatingSystem.IsLinux()) LinuxUpdate(payload); else overlay?.SendWebMessage(payload);
    });
    private void HideOnUi() { current = null; if (OperatingSystem.IsLinux()) LinuxHide(); timer?.Cancel(); timer?.Dispose(); timer = null; var old = overlay; overlay = null; old?.Close(); }
    public void Dispose() => HideOnUi();
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("warframe_overlay", EntryPoint = "wfc_overlay_show", CallingConvention = CallingConvention.Cdecl)] private static extern int LinuxShow([MarshalAs(UnmanagedType.LPUTF8Str)] string url, [MarshalAs(UnmanagedType.LPUTF8Str)] string payload, int x, int y, int width, int height, int seconds, int backend);
    [DllImport("warframe_overlay", EntryPoint = "wfc_overlay_hide", CallingConvention = CallingConvention.Cdecl)] private static extern void LinuxHide();
    [DllImport("warframe_overlay", EntryPoint = "wfc_overlay_update", CallingConvention = CallingConvention.Cdecl)] private static extern void LinuxUpdate([MarshalAs(UnmanagedType.LPUTF8Str)] string payload);
}
