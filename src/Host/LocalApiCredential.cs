using System.Security.Cryptography;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WarframeCompanion.Host;

public static class LocalApiCredential
{
    public static string PathFor(string directory, int port) => Path.Combine(directory, $"api-{port}.auth");
    public static string Read(string directory, int port)
    {
        var text = File.ReadAllText(PathFor(directory, port)).Trim();
        if (!text.StartsWith("Authorization: Bearer ", StringComparison.Ordinal) || text.Length != 86)
            throw new IOException("Local API credential is unavailable; start the companion first.");
        return text["Authorization: ".Length..];
    }
    internal static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal static bool Matches(string? supplied, string expected) => supplied != null && supplied.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    internal static void Write(string directory, int port, string token)
    {
        var path = PathFor(directory, port); var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                if (OperatingSystem.IsWindows())
                {
                    var security = new FileSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(temporary).SetAccessControl(security);
                }
                using var writer = new StreamWriter(stream);
                writer.Write("Authorization: Bearer " + token);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
