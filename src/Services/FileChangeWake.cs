namespace WarframeCompanion.Services;

// Notifications wake the single game loop. File cursors and confirmed syncs remain
// authoritative when the OS combines, duplicates or drops notifications.
internal sealed class FileChangeWake : IDisposable
{
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object gate = new();
    private FileSystemWatcher? log;
    private FileSystemWatcher? inventory;
    private int failed;
    private int inventoryChanged;
    private bool disposed;

    internal void Watch(string? logPath, string? inventoryPath)
    {
        if (Interlocked.Exchange(ref failed, 0) != 0)
        {
            log?.Dispose(); log = null;
            inventory?.Dispose(); inventory = null;
        }
        Configure(ref log, logPath, false);
        Configure(ref inventory, inventoryPath, true);
    }

    private void Configure(ref FileSystemWatcher? watcher, string? path, bool isInventory)
    {
        try
        {
            var full = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            if (watcher != null && Path.Combine(watcher.Path, watcher.Filter) == full && Directory.Exists(watcher.Path)) return;
            watcher?.Dispose();
            watcher = null;
            if (full == null || Path.GetDirectoryName(full) is not { } directory || !Directory.Exists(directory)) return;
            watcher = new(directory, Path.GetFileName(full)) {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += (_, _) => Signal(isInventory);
            watcher.Created += (_, _) => Signal(isInventory);
            watcher.Deleted += (_, _) => Signal(isInventory);
            watcher.Renamed += (_, _) => Signal(isInventory);
            watcher.Error += (_, _) => { Interlocked.Exchange(ref failed, 1); Signal(isInventory); };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            watcher = null;
        }
    }

    private void Signal(bool isInventory)
    {
        if (isInventory) Interlocked.Exchange(ref inventoryChanged, 1);
        lock (gate)
        {
            if (!disposed && wake.CurrentCount == 0) wake.Release();
        }
    }

    internal bool TakeInventoryChange() => Interlocked.Exchange(ref inventoryChanged, 0) != 0;

    internal async Task Wait(TimeSpan timeout, CancellationToken token)
    {
        if (!await wake.WaitAsync(timeout, token)) return;
        // A busy log may be written many times per frame. Use a fixed batching
        // interval so continuous writes cannot postpone reconciliation indefinitely.
        await Task.Delay(200, token);
        wake.Wait(0);
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        log?.Dispose();
        inventory?.Dispose();
        wake.Dispose();
    }
}
