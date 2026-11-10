using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed record MemoryCapture(InventorySnapshot Snapshot, string[] Sections);
public static class MemoryProvider
{
    public static int? GamePid()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process) try { if (process.ProcessName.Equals("Warframe.x64", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("Warframe.x64.exe", StringComparison.OrdinalIgnoreCase) || OperatingSystem.IsLinux() && process.ProcessName.Equals("Warframe.x64.ex", StringComparison.OrdinalIgnoreCase)) return process.Id; } catch (InvalidOperationException) { }
        }
        return null;
    }
    public static MemoryCapture? Decode(ReadOnlySpan<byte> bytes, int marker, string account, Action<string>? diagnostic = null)
    {
        ReadOnlySpan<byte> needle = "\"LastInventorySync\""u8;
        if (marker < 0 || marker > bytes.Length - needle.Length || !bytes.Slice(marker, needle.Length).SequenceEqual(needle)) return null;
        var starts = new List<int>();
        var first = 0;
        while (first < bytes.Length && char.IsWhiteSpace((char)bytes[first])) first++;
        if (first < bytes.Length && bytes[first] == ',') first++;
        if (first <= marker) starts.Add(first);
        var keys = InventoryParser.CountedKeys.Concat(InventoryParser.EquipmentKeys)
            .Concat(["XPInfo", "LastInventorySync", "PendingRecipes", "Upgrades", "Affiliations", "DailyFocus", "Missions", "PlayerSkills"]);
        foreach (var key in keys)
        {
            var at = bytes[..(marker + needle.Length)].IndexOf(Encoding.UTF8.GetBytes("\"" + key + "\""));
            if (at >= 0) starts.Add(at);
        }
        foreach (var start in starts.Distinct().Order().Take(64))
        {
            var addBrace = bytes[start] != '{';
            var prefix = addBrace ? 1 : 0;
            var input = new byte[bytes.Length - start + prefix];
            if (addBrace) input[0] = (byte)'{';
            bytes[start..].CopyTo(input.AsSpan(prefix));
            try
            {
                var reader = new Utf8JsonReader(input, new JsonReaderOptions { MaxDepth = 64 });
                using var doc = JsonDocument.ParseValue(ref reader);
                if (reader.BytesConsumed < marker - start + prefix + needle.Length) continue;
                var root = doc.RootElement;
                var structural = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("LastInventorySync", out _)
                    && (root.TryGetProperty("MiscItems", out _) || root.TryGetProperty("Suits", out _));
                // An intact object containing the hit must not be reinterpreted as a nested inventory.
                if (!structural) { diagnostic?.Invoke("Parsed object lacks top-level inventory sections."); if (!addBrace) return null; continue; }
                var tokens = new Utf8JsonReader(input.AsSpan(0, checked((int)reader.BytesConsumed)));
                var topLevelHit = false;
                while (tokens.Read())
                    if (tokens.TokenStartIndex == marker - start + prefix)
                    {
                        topLevelHit = tokens.TokenType == JsonTokenType.PropertyName && tokens.CurrentDepth == 1 && tokens.ValueTextEquals("LastInventorySync");
                        break;
                    }
                if (!topLevelHit) { diagnostic?.Invoke("Inventory marker is nested rather than top-level."); if (!addBrace) return null; continue; }
                // Only the allowlisted normalized snapshot crosses the persistence boundary.
                var parsed = InventoryParser.Parse(input.AsMemory(0, checked((int)reader.BytesConsumed)), account);
                var sections = root.EnumerateObject().Select(p => p.Name).Where(n => InventoryParser.CountedKeys.Contains(n)
                    || InventoryParser.EquipmentKeys.Contains(n) || n is "XPInfo" or "Upgrades" or "PendingRecipes").ToArray();
                return new(parsed with { Provider = "game memory" }, sections);
            }
            catch (Exception error) when (error is JsonException or InventoryException)
            {
                diagnostic?.Invoke(error is JsonException json ? $"Invalid JSON at byte {json.BytePositionInLine}." : error.Message);
            }
        }
        return null;
    }
    public static MemoryCapture? Prefer(MemoryCapture? current, MemoryCapture candidate, Func<MemoryCapture, bool>? acceptable = null)
    {
        if (current == null) return candidate;
        var a = acceptable?.Invoke(candidate) ?? true; var b = acceptable?.Invoke(current) ?? true;
        if (a != b) return a ? candidate : current;
        return candidate.Snapshot.SourceTimestamp > current.Snapshot.SourceTimestamp || candidate.Snapshot.SourceTimestamp == current.Snapshot.SourceTimestamp && candidate.Sections.Length > current.Sections.Length ? candidate : current;
    }
    public static IEnumerable<(long Start, long Length)> JoinRegions(IEnumerable<(long Start, long Length)> regions)
    {
        (long Start, long Length)? pending = null;
        foreach (var region in regions)
        {
            if (pending is { } previous && previous.Start + previous.Length == region.Start) pending = (previous.Start, checked(previous.Length + region.Length));
            else { if (pending is { } ready) yield return ready; pending = region; }
        }
        if (pending is { } last) yield return last;
    }
    public static MemoryCapture Scan(int pid, string account, CancellationToken cancellation, Func<MemoryCapture, bool>? acceptable = null, Action<string>? diagnostic = null)
    {
        MemoryCapture? best = null;
        Exception? callbackFailure = null;
        var candidates = 0;
        ulong scanned = 0;
        // Native callbacks are synchronous. Never allow a managed exception to cross the C ABI.
        CandidateCallback candidate = (pointer, size, marker, _) =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                candidates++;
                diagnostic?.Invoke($"Candidate bytes: {size}; marker offset: {marker}.");
                if (size > InventoryParser.MaxBytes || marker >= size) return 1;
                var bytes = new byte[checked((int)size)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                var capture = Decode(bytes, checked((int)marker), account, diagnostic);
                if (capture != null) best = Prefer(best, capture, acceptable);
                return 1;
            }
            catch (Exception error) { callbackFailure = error; return 0; }
        };
        ProgressCallback progress = (bytes, _) => { scanned = bytes; return cancellation.IsCancellationRequested ? 0 : 1; };
        var result = ScanNative(pid, candidate, progress, 0);
        GC.KeepAlive(candidate);
        GC.KeepAlive(progress);
        cancellation.ThrowIfCancellationRequested();
        if (callbackFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackFailure).Throw();
        if (result < 0) throw new InventoryException(result == -1
            ? "The operating system denied read-only game memory access."
            : "The native memory scan failed; confirmed inventory was retained.");
        return best ?? throw new InventoryException(result == 2
            ? "No complete inventory found within the scan budget; retry after a game sync."
            : $"No usable inventory in {candidates} candidate buffers across {scanned / (1024 * 1024)} MiB. Confirmed inventory was retained.");
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CandidateCallback(nint bytes, nuint size, nuint marker, nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProgressCallback(ulong scanned, nint context);
    [DllImport("warframe_native", EntryPoint = "wfc_scan_memory", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScanNative(int pid, CandidateCallback candidate, ProgressCallback progress, nint context);
}
