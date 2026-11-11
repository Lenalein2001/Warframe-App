using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarframeCompanion.Core;
using WarframeCompanion.Services;

namespace WarframeCompanion.Host;

public sealed class CompanionServer : IAsyncDisposable
{
    private readonly WebApplication app;
    public CompanionRuntime Runtime { get; }
    public string Url { get; }
    public string LaunchUrl { get; private set; } = "";
    private Action? clearCredential;
    private CompanionServer(WebApplication app, CompanionRuntime runtime, string url) { this.app = app; Runtime = runtime; Url = url; }
    public static async Task<CompanionServer> Start(string directory, bool offline = false, int? port = null, CancellationToken cancellation = default, bool migrateLegacy = false, bool watchGame = true)
    {
        var runtime = new CompanionRuntime(directory, offline, migrateLegacy); var selectedPort = port ?? (int)J.N(runtime.GetSettings(), "port", 8642);
        if (selectedPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var url = "http://127.0.0.1:" + selectedPort;
        var token = LocalApiCredential.Create();
        var cookie = "wfc-session-" + selectedPort;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, selectedPort); k.Limits.MaxRequestBodySize = InventoryParser.MaxBytes; k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); });
        var app = builder.Build(); runtime.QuitRequested += () => app.Lifetime.StopApplication();
        var assembly = typeof(CompanionServer).Assembly;
        var assets = assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)).ToDictionary(n => n[4..].Replace('\\', '/'), n => n, StringComparer.Ordinal);
        var types = new FileExtensionContentTypeProvider(); types.Mappings[".ttf"] = "font/ttf";
        app.Run(async context =>
        {
            var request = context.Request; var response = context.Response;
            async Task Json(int status, object value) { response.StatusCode = status; response.ContentType = "application/json"; await JsonSerializer.SerializeAsync(response.Body, value, J.Options, context.RequestAborted); }
            if ((request.Host.Host != "127.0.0.1" && request.Host.Host != "localhost") || request.Host.Port != selectedPort) { await Json(403, new { error = "Forbidden host." }); return; }
            response.Headers.CacheControl = "no-store";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            if (request.Headers["Sec-Fetch-Site"] == "cross-site") { await Json(403, new { error = "Forbidden site." }); return; }
            if (request.Method is not ("GET" or "HEAD" or "POST")) { await Json(405, new { error = "Method not allowed." }); return; }
            if (request.Method == "POST" && request.Headers.Origin.Count > 0 && request.Headers.Origin.ToString() != "http://" + request.Host) { await Json(403, new { error = "Forbidden origin." }); return; }
            var path = request.Path.Value ?? "/";
            if (path == "/session")
            {
                if (request.Method != "GET" || !LocalApiCredential.Matches(request.Query["token"], token)) { await Json(401, new { error = "Authentication required." }); return; }
                response.Cookies.Append(cookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
                var bridge = request.Query["overlay"].ToString();
                response.Redirect(bridge is "native" or "photino" ? "/overlay.html?bridge=" + bridge : "/"); return;
            }
            if (path.StartsWith("/api/", StringComparison.Ordinal) &&
                !LocalApiCredential.Matches(request.Headers.Authorization, "Bearer " + token) &&
                !LocalApiCredential.Matches(request.Cookies[cookie], token))
            { await Json(401, new { error = "Authentication required." }); return; }
            try
            {
                if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    var api = path[5..]; JsonNode result;
                    if (api == "art" && request.Method != "POST")
                    {
                        var bytes = await runtime.Art.Get(request.Query["u"].ToString(), int.TryParse(request.Query["s"], out var size) ? size : 128, context.RequestAborted);
                        response.ContentType = "image/png"; response.ContentLength = bytes.Length; response.Headers.CacheControl = "private, max-age=86400";
                        if (request.Method != "HEAD") await response.Body.WriteAsync(bytes, context.RequestAborted); return;
                    }
                    if (request.Method == "POST")
                    {
                        var body = request.ContentLength == 0 || request.ContentLength == null && !request.Headers.ContainsKey("Transfer-Encoding") ? new JsonObject() : await JsonNode.ParseAsync(request.Body, new JsonNodeOptions(), new JsonDocumentOptions { MaxDepth = 64 }, context.RequestAborted) ?? new JsonObject();
                        result = await runtime.Post(api, body, context.RequestAborted);
                    }
                    else result = runtime.Get(api, request.Query.ToDictionary(p => p.Key, p => p.Value.FirstOrDefault() ?? ""));
                    if (request.Method != "HEAD") await Json(200, result); return;
                }
                if (request.Method == "POST") { await Json(405, new { error = "Method not allowed." }); return; }
                var name = path == "/" ? "index.html" : path.TrimStart('/');
                if (!assets.TryGetValue(name, out var resource)) { response.StatusCode = 404; return; }
                response.ContentType = types.TryGetContentType(name, out var contentType) ? contentType : "application/octet-stream";
                await using var stream = assembly.GetManifestResourceStream(resource)!; response.ContentLength = stream.Length;
                if (name == "overlay.html" && (request.Query["bridge"] == "photino" || request.Query["bridge"] == "native"))
                {
                    using var reader = new StreamReader(stream); var html = await reader.ReadToEndAsync(context.RequestAborted);
                    const string bridge = "<script>window.ipc={postMessage:m=>window.external.sendMessage(m)};window.external.receiveMessage(m=>{const d=JSON.parse(m);window.render(d.kind,d.result,d.unit,d.boxes);});</script>";
                    html = html.Replace("<head>", "<head>" + (request.Query["bridge"] == "photino" ? bridge : "<script>window.ipc={postMessage:()=>{}};</script>"), StringComparison.Ordinal); response.ContentLength = null;
                    if (request.Method != "HEAD") await response.WriteAsync(html, context.RequestAborted); return;
                }
                if (request.Method != "HEAD") await stream.CopyToAsync(response.Body, context.RequestAborted);
            }
            catch (InventoryException e) { await Json(400, new { error = e.Message }); }
            catch (NotSupportedException e) { await Json(501, new { error = e.Message }); }
            catch (JsonException) { await Json(400, new { error = "Invalid JSON body." }); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or HttpRequestException)
            { await Json(503, new { error = "Operation failed. Existing confirmed data was retained; check connectivity, storage and permissions." }); }
        });
        try
        {
            await app.StartAsync(cancellation); LocalApiCredential.Write(directory, selectedPort, token); runtime.Start(watchGame);
            return new(app, runtime, url) {
                LaunchUrl = url + "/session?token=" + token,
                clearCredential = () => {
                    try { if (LocalApiCredential.Read(directory, selectedPort) == "Bearer " + token) File.Delete(LocalApiCredential.PathFor(directory, selectedPort)); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            };
        }
        catch { await app.DisposeAsync(); await runtime.DisposeAsync(); throw; }
    }
    public Task WaitForShutdown(CancellationToken cancellation = default) => app.WaitForShutdownAsync(cancellation);
    public async ValueTask DisposeAsync() { try { await app.StopAsync(); await Runtime.DisposeAsync(); await app.DisposeAsync(); } finally { clearCredential?.Invoke(); } }
}
