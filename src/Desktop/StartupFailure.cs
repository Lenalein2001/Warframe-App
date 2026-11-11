using System.Runtime.InteropServices;
using WarframeCompanion.Host;

namespace WarframeCompanion.Desktop;

internal static class StartupFailure
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    public static void Report(Exception error, string? directory, bool interactive)
    {
        var message = error is ProfileInUseException
            ? "This profile is already open. Wait for the companion to finish starting, or close its other instance before trying again."
            : "Warframe Companion could not start. Check that the entire package was extracted, its required runtimes are installed, and the selected data folder and port are available.";
        var diagnostic = $"{DateTimeOffset.UtcNow:O} {error.GetType().Name}: {message}";
        Console.Error.WriteLine(diagnostic);
        if (directory != null && error is not ProfileInUseException)
        {
            try { File.WriteAllText(Path.Combine(directory, "startup-error.log"), diagnostic + Environment.NewLine); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (interactive && OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, message, "Warframe Companion", 0x10);
    }
}
