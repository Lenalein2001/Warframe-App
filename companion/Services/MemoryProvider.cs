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
    public static MemoryCapture? Decode(ReadOnlySpan<byte> bytes, int marker, string account)
    {
        var starts = new List<int>(); var first = 0; while (first < bytes.Length && char.IsWhiteSpace((char)bytes[first])) first++;
        if (first < bytes.Length && bytes[first] == '{') starts.Add(first);
        // A game allocation can lose its first fields. Recover only plausible top-level section starts.
        var keys = InventoryParser.CountedKeys.Concat(InventoryParser.EquipmentKeys).Concat(["XPInfo", "LastInventorySync", "PendingRecipes", "Upgrades"]);
        foreach (var key in keys)
        {
            var needle = Encoding.UTF8.GetBytes("\"" + key + "\""); var at = bytes[..Math.Min(bytes.Length, marker)].IndexOf(needle);
            if (at >= 0) starts.Add(at);
        }
        foreach (var start in starts.Distinct().Order().Take(64))
        {
            var addBrace = bytes[start] != '{'; var input = new byte[bytes.Length - start + (addBrace ? 1 : 0)]; if (addBrace) input[0] = (byte)'{'; bytes[start..].CopyTo(input.AsSpan(addBrace ? 1 : 0));
            try
            {
                var reader = new Utf8JsonReader(input, new JsonReaderOptions { MaxDepth = 64 });
                if (!JsonDocument.TryParseValue(ref reader, out var doc) || reader.BytesConsumed <= marker - start) continue;
                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("LastInventorySync", out _) || !(root.TryGetProperty("MiscItems", out _) || root.TryGetProperty("Suits", out _))) continue;
                    // Parser discards unknown fields; raw bytes and credentials never reach persistence or diagnostics.
                    var parsed = InventoryParser.Parse(input.AsMemory(0, checked((int)reader.BytesConsumed)), account);
                    return new(parsed with { Provider = "game memory" }, root.EnumerateObject().Select(p => p.Name).Where(n => InventoryParser.CountedKeys.Contains(n) || InventoryParser.EquipmentKeys.Contains(n) || n is "XPInfo" or "Upgrades" or "PendingRecipes").ToArray());
                }
            }
            catch (Exception e) when (e is JsonException or InventoryException) { }
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
    public static MemoryCapture Scan(int pid, string account, CancellationToken cancellation, Func<MemoryCapture, bool>? acceptable = null)
    {
        MemoryCapture? best = null;
        Exception? callbackFailure = null;
        // Native callbacks are synchronous. Never allow a managed exception to cross the C ABI.
        CandidateCallback candidate = (pointer, size, marker, _) =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (size > InventoryParser.MaxBytes || marker >= size) return 1;
                var bytes = new byte[checked((int)size)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                var capture = Decode(bytes, checked((int)marker), account);
                if (capture != null) best = Prefer(best, capture, acceptable);
                return 1;
            }
            catch (Exception error) { callbackFailure = error; return 0; }
        };
        ProgressCallback progress = (_, _) => cancellation.IsCancellationRequested ? 0 : 1;
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
            : "Inventory text is not available in game memory. Confirmed inventory was retained.");
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CandidateCallback(nint bytes, nuint size, nuint marker, nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProgressCallback(ulong scanned, nint context);
    [DllImport("warframe_native", EntryPoint = "wfc_scan_memory", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ScanNative(int pid, CandidateCallback candidate, ProgressCallback progress, nint context);
}
