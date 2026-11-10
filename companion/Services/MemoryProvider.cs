using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

public sealed record MemoryCapture(InventorySnapshot Snapshot, string[] Sections);
public static class MemoryProvider
{
    private static readonly byte[] Marker = "\"LastInventorySync\""u8.ToArray();
    public static int? GamePid()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process) try { if (process.ProcessName.Equals("Warframe.x64", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("Warframe.x64.exe", StringComparison.OrdinalIgnoreCase)) return process.Id; } catch (InvalidOperationException) { }
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
        using var process = new Reader(pid); var watch = Stopwatch.StartNew(); var buffer = new byte[4 * 1024 * 1024 + Marker.Length]; MemoryCapture? best = null; long scanned = 0;
        foreach (var (start, length) in JoinRegions(process.Regions()))
        {
            for (long cursor = start; cursor < start + length;)
            {
                cancellation.ThrowIfCancellationRequested(); if (watch.Elapsed > TimeSpan.FromSeconds(45) || scanned > 32L * 1024 * 1024 * 1024) return best ?? throw new InventoryException("No complete inventory found within the scan budget; retry after a game sync.");
                var wanted = (int)Math.Min(buffer.Length, start + length - cursor); var read = process.Read(cursor, buffer, wanted);
                if (read == 0) { cursor += 4096; continue; } scanned += read;
                var offset = 0;
                while (offset < read && NativeEngine.Find(buffer, read, Marker, offset) is var hit && hit >= 0)
                {
                    hit += offset; var address = cursor + hit; var begin = Math.Max(start, address - 8 * 1024 * 1024); var extent = (int)Math.Min(InventoryParser.MaxBytes, start + length - begin);
                    var blob = new byte[extent]; var got = process.Read(begin, blob, extent); var marker = (int)(address - begin);
                    if (got > marker)
                    {
                        var before = blob.AsSpan(0, marker).LastIndexOf((byte)0); var after = blob.AsSpan(marker, got - marker).IndexOf((byte)0); var stop = after < 0 ? got : marker + after;
                        var capture = Decode(blob.AsSpan(before + 1, stop - before - 1), marker - before - 1, account);
                        if (capture != null) best = Prefer(best, capture, acceptable);
                    }
                    offset = hit + Marker.Length;
                }
                cursor += Math.Max(4096, read - Marker.Length);
            }
        }
        return best ?? throw new InventoryException("Inventory text is not available in game memory. Confirmed inventory was retained.");
    }
    private sealed class Reader : IDisposable
    {
        private readonly SafeProcessHandle? handle;
        private readonly FileStream? stream;
        private readonly int pid;
        public Reader(int pid)
        {
            this.pid = pid;
            if (OperatingSystem.IsWindows()) { handle = OpenProcess(0x410, false, pid); if (handle.IsInvalid) { handle.Dispose(); throw new InventoryException("Windows denied read-only access to Warframe."); } }
            else try { stream = new FileStream($"/proc/{pid}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite); } catch (UnauthorizedAccessException) { throw new InventoryException("The operating system denied read-only game memory access."); }
        }
        public IEnumerable<(long Start, long Length)> Regions()
        {
            if (handle != null)
            {
                long address = 0;
                while (VirtualQueryEx(handle, (nint)address, out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) != 0)
                {
                    var start = info.BaseAddress.ToInt64(); var length = checked((long)info.RegionSize); if (length <= 0 || start > long.MaxValue - length) yield break;
                    if (info.State == 0x1000 && info.Type == 0x20000 && (info.Protect & 0x104) == 4) yield return (start, length);
                    address = start + length;
                }
            }
            else foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
            {
                var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (columns.Length < 2 || !columns[1].StartsWith("rw", StringComparison.Ordinal) || line.Contains("/dev/", StringComparison.Ordinal) || line.Contains(".dll", StringComparison.OrdinalIgnoreCase) || line.Contains(".so", StringComparison.OrdinalIgnoreCase) || line.Contains(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var range = columns[0].Split('-'); var start = long.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture); var end = long.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture); yield return (start, end - start);
            }
        }
        public int Read(long address, byte[] buffer, int count)
        {
            if (handle != null) { ReadProcessMemory(handle, (nint)address, buffer, (nuint)count, out var read); return checked((int)read); }
            try { stream!.Position = address; return stream.Read(buffer, 0, count); } catch (IOException) { return 0; }
        }
        public void Dispose() { handle?.Dispose(); stream?.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo { public nint BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public nuint RegionSize; public uint State, Protect, Type; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint count, out nuint read);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryInfo info, nuint size);
}
