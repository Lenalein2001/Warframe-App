using System.Diagnostics;
using System.Globalization;

namespace WarframeCompanion.Desktop;

// Only fixed stage names and elapsed milliseconds. No paths, URLs, tokens or account data.
internal sealed class StartupTiming
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly List<string> entries = [];
    private readonly HashSet<string> stages = [];
    private readonly object sync = new();
    private string? file;
    public StartupTiming()
    {
        using var process = Process.GetCurrentProcess();
        var runtime = Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds);
        entries.Add("process-to-managed-entry-ms=" + runtime.ToString("F1", CultureInfo.InvariantCulture));
        if (long.TryParse(Environment.GetEnvironmentVariable("WFC_LAUNCH_FILETIME"), out var launch) && launch > 0 && launch <= DateTime.UtcNow.ToFileTimeUtc())
        {
            var elapsed = (DateTime.UtcNow - DateTime.FromFileTimeUtc(launch)).TotalMilliseconds;
            entries.Add("launcher-to-managed-entry-ms=" + elapsed.ToString("F1", CultureInfo.InvariantCulture));
        }
        if (long.TryParse(Environment.GetEnvironmentVariable("WFC_CREATE_FILETIME"), out var requested) && requested > 0 && requested <= DateTime.UtcNow.ToFileTimeUtc())
            entries.Add("create-process-to-managed-entry-ms=" + (DateTime.UtcNow - DateTime.FromFileTimeUtc(requested)).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
        Mark("managed-entry");
    }
    public void Attach(string directory)
    {
        lock (sync) { file = Path.Combine(directory, "startup-timing.log"); Flush(); }
    }
    public void Mark(string stage)
    {
        lock (sync)
        {
            if (!stages.Add(stage)) return;
            entries.Add(watch.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + "ms " + stage);
            Flush();
        }
    }
    private void Flush()
    {
        if (file == null) return;
        try { File.WriteAllLines(file, entries); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
