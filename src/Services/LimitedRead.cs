using WarframeCompanion.Core;

namespace WarframeCompanion.Services;

internal static class LimitedRead
{
    // Enforce the limit while reading, including unknown/changing content lengths.
    internal static async Task<byte[]> Bytes(Stream input, int limit, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(16384, limit + 1)];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - output.Length + 1)), cancellation);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit) throw new InventoryException("Input exceeds the permitted size; existing data was retained.");
            output.Write(buffer, 0, read);
        }
    }
}
