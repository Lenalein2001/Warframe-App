using System.Security.Cryptography;

namespace WarframeCompanion.Desktop;

internal static class DesktopAssets
{
    internal static string Icon(string profile)
    {
        var name = OperatingSystem.IsWindows() ? "icon.ico" : "icon.png";
        using var resource = typeof(DesktopAssets).Assembly.GetManifestResourceStream(name)!;
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var content = bytes.ToArray();
        var directory = Path.Combine(profile, "cache", "assets");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(content))[..16] + "-" + name);
        if (!File.Exists(path))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temp, content); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return path;
    }
}
