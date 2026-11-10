using System.Text.Json.Nodes;
using SkiaSharp;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed record OverlayRequest(string Kind, JsonNode Result, int X, int Y, int Width, int Height, double Unit, int Seconds, bool Above, string Backend = "auto");
public sealed partial class CompanionRuntime
{
    private readonly SemaphoreSlim ocrGate = new(1);
    private JsonNode rewards = new JsonObject();
    private JsonNode riven = new JsonObject();
    private long rewardGeneration;
    private Task? automaticOcr;
    public event Action<OverlayRequest>? OverlayRequested;
    public event Action<string, JsonNode>? OverlayUpdated;
    public event Action? OverlayHidden;
    public Func<JsonNode>? RegisterHotkeys { get; set; }
    public Func<JsonNode>? HotkeyStatus { get; set; }
    private void StartAutomaticOcr()
    {
        if (!J.B(GetSettings()["ocr_enabled"]) || automaticOcr is { IsCompleted: false }) return;
        automaticOcr = ScanRewards(true, stop.Token);
    }
    private void CloseRewards() { Interlocked.Increment(ref rewardGeneration); OverlayHidden?.Invoke(); }
    private GameView PlayingView()
    {
        lock (sync) { var account = registry.Accounts.FirstOrDefault(a => a.Key == playingKey) ?? Active; return new(Catalog, account == null ? null : snapshots.Current(account.Id ?? account.Key), account?.Profile, account?.Manual ?? new(), Market); }
    }
    public async Task<JsonNode> ScanRewards(bool automatic, CancellationToken cancellation)
    {
        if (!await ocrGate.WaitAsync(0, cancellation)) { lock (sync) return rewards.DeepClone(); }
        var generation = Interlocked.Read(ref rewardGeneration); var started = J.Now; CapturedScreen? screen = null;
        try
        {
            OverlayHidden?.Invoke(); var config = GetSettings(); RewardLayout? layout;
            do
            {
                screen?.Dispose(); screen = await ScreenCapture.Capture(J.S(config, "screenshot_tool") ?? "auto", cancellation); layout = NativeEngine.Layout(screen.Bitmap, Log.Scale);
                if (layout != null || !automatic || J.Now - started > 12 || generation != Interlocked.Read(ref rewardGeneration)) break;
                await Task.Delay(250, cancellation);
            } while (true);
            if (generation != Interlocked.Read(ref rewardGeneration)) return new JsonObject();
            var view = PlayingView(); var names = Catalog.Relics.Values.SelectMany(r => r.Rewards.Values).GroupBy(r => r.Name).Select(g => g.First()).ToDictionary(r => r.Name);
            var items = new List<JsonNode>(); var text = new List<string>(); var cards = layout?.Cards ?? 4; var unit = layout?.Unit ?? Math.Min(screen.Bitmap.Width / 1920.0, screen.Bitmap.Height / 1080.0) * (Log.Scale ?? 1);
            if (layout == null && automatic) throw new InventoryException("Reward cards did not become visible before the read timed out.");
            for (var slot = 0; slot < cards; slot++)
            {
                var width = layout == null ? screen.Bitmap.Width * .86 / 4 : 242 * unit;
                var left = screen.Bitmap.Width / 2.0 - cards * width / 2 + slot * width;
                var rectangle = new SKRectI((int)left, (int)(layout == null ? screen.Bitmap.Height * .27 : screen.Bitmap.Height / 2.0 - 150 * unit), (int)(left + width), (int)(layout == null ? screen.Bitmap.Height * .56 : screen.Bitmap.Height / 2.0 - 78 * unit));
                (string? Name, double Score) best = (null, 0);
                foreach (var variant in new[] { 2, 3, 4, 0 }) { var read = await ScreenCapture.Text(screen.Bitmap, rectangle, 6, variant, cancellation); text.Add(read); var match = OcrReader.Match(read, names.Keys); if (match.Score > best.Score) best = match; if (best.Score >= .98) break; }
                if (best.Name == null || best.Score < .9 && items.Any(i => J.S(i, "name") == best.Name)) continue;
                var reward = names[best.Name]; var uid = Catalog.Aliases.GetValueOrDefault(reward.Uid) ?? reward.Uid; var part = Catalog.Parts.GetValueOrDefault(uid); var slug = Market.Slug(uid, reward.Slug, reward.Name);
                var needed = part?.Items.Select(id => Catalog.Items.GetValueOrDefault(id)).OfType<GameItem>().Where(i => view.Status(i).Status != "mastered" && view.Owned(i.Uid) is null or 0).SelectMany(i => view.Parts(i)).Where(p => p.Uid == uid).Select(p => Math.Max(0, p.Need - p.Have)).DefaultIfEmpty().Max() ?? 0;
                items.Add(J.Node(new { name = reward.Name, uid, score = best.Score, slug, have = view.Count(uid), needed, vaulted = part?.Vaulted, ducats = Market.Item(slug)?.Ducats, price = Market.Price(slug)?.Sell, slot = layout == null ? (int?)null : slot }));
            }
            string? Best() => items.Where(i => J.N(i, "price") > 0).OrderByDescending(i => J.N(i, "price")).Select(i => J.S(i, "name")).FirstOrDefault();
            var result = J.Node(new { t = J.Now, items, best = Best(), ocr = string.Join('\n', text), error = items.Count == 0 ? "No reward names could be read." : layout == null ? "Unconfirmed screen: these names are not recorded in history." : null, cards = layout?.Cards, scale = layout?.Scale });
            lock (sync) rewards = result.DeepClone();
            if (layout != null && items.Count > 0 && generation == Interlocked.Read(ref rewardGeneration))
            {
                lock (sync) History.Record(result, playingKey ?? registry.Active);
                if (J.B(config["notify"])) Notify("reward:" + J.D(result["t"]), "Relic rewards", string.Join(" · ", items.Select(i => J.S(i, "name") + (J.Get(i, "price") != null ? " " + J.N(i, "price") + "p" : ""))));
                if (J.B(config["overlay_enabled"])) OverlayRequested?.Invoke(new("rewards", result.DeepClone(), screen.X, screen.Y, screen.Bitmap.Width, screen.Bitmap.Height, unit, (int)J.N(config, "overlay_seconds", 15), J.S(config, "overlay_position") == "above", J.S(config, "overlay_backend") ?? "auto"));
                var accountKey = playingKey ?? registry.Active;
                for (var attempt = 0; attempt < 8 && !Offline && generation == Interlocked.Read(ref rewardGeneration); attempt++)
                {
                    if (items.All(i => J.Get(i, "price") != null || J.S(i, "slug") == null)) break;
                    await Task.Delay(400, cancellation);
                    foreach (var item in items) if (Market.Price(J.S(item, "slug"), false)?.Sell is { } price) item["price"] = price;
                    result["items"] = J.Array(items); result["best"] = Best(); lock (sync) rewards = result.DeepClone();
                    History.Record(result, accountKey); OverlayUpdated?.Invoke("rewards", result.DeepClone());
                }
            }
            return result;
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or InventoryException or TaskCanceledException)
        { var error = J.Node(new { t = J.Now, items = Array.Empty<object>(), error = e is InventoryException ? e.Message : "Screen capture/OCR failed or timed out." }); lock (sync) rewards = error; return error.DeepClone(); }
        finally { screen?.Dispose(); ocrGate.Release(); }
    }
    public async Task<JsonNode> ScanRiven(CancellationToken cancellation)
    {
        await ocrGate.WaitAsync(cancellation);
        try
        {
            OverlayHidden?.Invoke(); var config = GetSettings(); using var frame = await ScreenCapture.Capture(J.S(config, "screenshot_tool") ?? "auto", cancellation);
            var unit = Math.Min(frame.Bitmap.Width / 1920.0, frame.Bitmap.Height / 1080.0) * (Log.Scale ?? 1); var cx = frame.Bitmap.Width / 2.0; var cy = frame.Bitmap.Height / 2.0;
            var crops = new[] { new SKRectI((int)(cx - 150 * unit), (int)(cy + 70 * unit), (int)(cx + 150 * unit), (int)(cy + 285 * unit)), new SKRectI(0, 0, frame.Bitmap.Width, frame.Bitmap.Height) };
            ParsedRiven best = new(null, []); var raw = "";
            foreach (var crop in crops)
            {
                foreach (var variant in new[] { 0, 1 }) { var text = await ScreenCapture.Text(frame.Bitmap, crop, 6, variant, cancellation); var read = OcrReader.ParseRiven(text, Catalog.Items.Values.Where(i => i.Disposition > 0).Select(i => i.Name)); if (read.Weapon != null && (best.Weapon == null || read.Stats.Length > best.Stats.Length)) { best = read; raw = text; } }
                if (best.Weapon != null && best.Stats.Length >= 2) break;
            }
            if (best.Weapon == null || best.Stats.Length == 0) throw new InventoryException("No weapon and riven stats could be read. Keep the card fully visible.");
            var weapon = RivenService.Weapon(Catalog, best.Weapon); var (pos, neg, unread) = RivenService.Infer(weapon, best.Stats);
            var graded = best.Stats.Select(s => RivenService.Roll(weapon, s.Stat, s.Value, pos, neg, s.Negative)).OfType<GradedStat>().ToArray();
            var result = J.Node(new { t = J.Now, weapon = weapon.Name, image = Catalog.ImageUrl(weapon.Image), positives = pos, negatives = neg, unread, stats = graded,
                overall = graded.Length > 0 && graded.All(g => !g.OutOfRange && !g.SignConflict) ? RivenService.Grade(graded.Average(g => g.Deviation)) : null, traded = Rivens.Traded(weapon.Name), market = (object?)null, marketError = (string?)null, ocr = raw, error = (string?)null });
            lock (sync) riven = result.DeepClone();
            if (J.B(config["riven_overlay_enabled"])) OverlayRequested?.Invoke(new("riven", result.DeepClone(), frame.X, frame.Y, frame.Bitmap.Width, frame.Bitmap.Height, unit, (int)J.N(config, "riven_overlay_seconds", 20), false, J.S(config, "overlay_backend") ?? "auto"));
            if (!Offline)
            {
                try { result["market"] = await Market.RivenListings(weapon, graded.Where(g => !g.Negative).Select(g => g.Stat), cancellation); }
                catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or HttpRequestException or TaskCanceledException) { result["marketError"] = "Riven listings are unavailable."; }
                lock (sync) riven = result.DeepClone();
                OverlayUpdated?.Invoke("riven", result.DeepClone());
            }
            return result;
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested && e is IOException or InventoryException or TaskCanceledException)
        { var error = J.Node(new { t = J.Now, stats = Array.Empty<object>(), error = e is InventoryException ? e.Message : "Screen capture/OCR failed or timed out." }); lock (sync) riven = error; return error.DeepClone(); }
        finally { ocrGate.Release(); }
    }
    private async Task TestOverlay(CancellationToken cancellation)
    {
        if (!Desktop || OverlayRequested == null) throw new InventoryException("Open the desktop app to show an overlay.");
        using var screen = await ScreenCapture.Capture(J.S(GetSettings(), "screenshot_tool") ?? "auto", cancellation);
        var result = J.Node(new { cards = 4, best = "Ash Prime Systems Blueprint", items = new[] { new { slot = 0, name = "Ash Prime Systems Blueprint", price = 25, ducats = 65, have = 0, needed = 1 }, new { slot = 1, name = "Braton Prime Barrel", price = 4, ducats = 15, have = 2, needed = 0 }, new { slot = 2, name = "Forma Blueprint", price = 0, ducats = 0, have = 3, needed = 0 }, new { slot = 3, name = "Fragor Prime Head", price = 6, ducats = 45, have = 0, needed = 1 } } });
        OverlayRequested.Invoke(new("rewards", result, screen.X, screen.Y, screen.Bitmap.Width, screen.Bitmap.Height, Math.Min(screen.Bitmap.Width / 1920.0, screen.Bitmap.Height / 1080.0) * (Log.Scale ?? 1), 15, J.S(GetSettings(), "overlay_position") == "above", J.S(GetSettings(), "overlay_backend") ?? "auto"));
    }
}
