namespace WarframeCompanion.Desktop;

internal static class DesktopPaths
{
    internal static string UserDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarframeCompanion");
    internal static string DefaultDirectory(bool portable = false)
    {
        var adjacent = Path.Combine(AppContext.BaseDirectory, "data");
        return portable || File.Exists(Path.Combine(adjacent, "state.sqlite")) || File.Exists(Path.Combine(adjacent, "companion.sqlite"))
            ? adjacent : UserDirectory;
    }
}
