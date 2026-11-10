using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace WarframeCompanion.Services;

public static class NativeEngine
{
    public static (ulong Device, ulong File) FileIdentity(string path)
    {
        if (FileIdentityNative(path, out var device, out var file) != 1) throw new IOException("Cannot identify the game log file.");
        return (device, file);
    }
    [DllImport("warframe_native", EntryPoint = "wfc_file_identity", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FileIdentityNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out ulong device, out ulong file);
    static NativeEngine() { if (Version() != 1) throw new IOException("Native engine ABI does not match the application."); }
    public static byte[] Unlzma(byte[] input)
    {
        var output = new byte[8 * 1024 * 1024]; var size = UnlzmaNative(input, (nuint)input.Length, output, (nuint)output.Length);
        if (size < 0) throw new IOException("Invalid or oversized LZMA export index."); return output.AsSpan(0, checked((int)size)).ToArray();
    }
    public static double Similarity(string a, string b)
    {
        var aa = Encoding.UTF8.GetBytes(a); var bb = Encoding.UTF8.GetBytes(b); var score = SimilarityNative(aa, (nuint)aa.Length, bb, (nuint)bb.Length);
        if (score < 0) throw new ArgumentException("OCR match exceeds the native engine bounds."); return score;
    }
    public static int Find(byte[] data, int length, byte[] needle, int offset = 0)
    {
        if (offset < 0 || length < offset || length > data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { return checked((int)FindNative(pinned.AddrOfPinnedObject() + offset, (nuint)(length - offset), needle, (nuint)needle.Length)); }
        finally { pinned.Free(); }
    }
    public static void Prepare(SKBitmap bitmap, int variant)
    {
        if (bitmap.ColorType != SKColorType.Bgra8888) throw new ArgumentException("OCR requires BGRA pixels.");
        if (PrepareNative(bitmap.GetPixels(), bitmap.Width, bitmap.Height, bitmap.RowBytes, variant) != 1) throw new IOException("Invalid OCR pixel buffer.");
    }
    public static RewardLayout? Layout(SKBitmap bitmap, double? scale)
    {
        using var converted = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var frame = converted ?? bitmap;
        var cards = LayoutNative(frame.GetPixels(), frame.Width, frame.Height, frame.RowBytes, scale ?? 0, out var found);
        if (cards < 0) throw new IOException("Invalid reward pixel buffer.");
        return cards == 0 ? null : new(cards, found, Math.Min(frame.Width / 1920.0, frame.Height / 1080.0) * found);
    }
    [DllImport("warframe_native", EntryPoint = "wfc_abi_version", CallingConvention = CallingConvention.Cdecl)] private static extern int Version();
    [DllImport("warframe_native", EntryPoint = "wfc_unlzma", CallingConvention = CallingConvention.Cdecl)] private static extern long UnlzmaNative(byte[] data, nuint size, byte[] output, nuint capacity);
    [DllImport("warframe_native", EntryPoint = "wfc_find", CallingConvention = CallingConvention.Cdecl)] private static extern long FindNative(nint data, nuint size, byte[] needle, nuint count);
    [DllImport("warframe_native", EntryPoint = "wfc_prepare_bgra", CallingConvention = CallingConvention.Cdecl)] private static extern int PrepareNative(nint pixels, int width, int height, int stride, int threshold);
    [DllImport("warframe_native", EntryPoint = "wfc_similarity", CallingConvention = CallingConvention.Cdecl)] private static extern double SimilarityNative(byte[] a, nuint na, byte[] b, nuint nb);
    [DllImport("warframe_native", EntryPoint = "wfc_reward_layout", CallingConvention = CallingConvention.Cdecl)] private static extern int LayoutNative(nint pixels, int width, int height, int stride, double scale, out double found);
}
