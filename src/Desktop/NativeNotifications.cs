using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace WarframeCompanion.Desktop;

// OS delivery runs on one bounded worker, never the UI or account-state lock.
internal sealed class NativeNotifications : IDisposable
{
    private readonly BlockingCollection<(string Title, string Body, TaskCompletionSource Completion)> queue = new(32);
    private readonly Thread worker;
    public NativeNotifications()
    {
        worker = new Thread(Work) { IsBackground = true, Name = "Companion notifications" };
        worker.Start();
    }
    public void Show(string title, string body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryAdd((title, body, completion))) throw new IOException("Notification queue is full.");
        try { completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch (TimeoutException e) { throw new IOException("The desktop notification service did not respond.", e); }
    }
    private void Work()
    {
        var initialized = false;
        try
        {
            foreach (var (title, body, completion) in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        if (!initialized) { Marshal.ThrowExceptionForHR(WindowsInit()); initialized = true; }
                        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + body)))[..16];
                        Marshal.ThrowExceptionForHR(WindowsShow(title, body, tag));
                    }
                    else
                    {
                        if (!initialized) { if (LinuxInit("Warframe Companion") == 0) throw new IOException("Notification service unavailable."); initialized = true; }
                        var notification = LinuxNew(title, SecurityElement.Escape(body) ?? "", "applications-games");
                        if (notification == 0) throw new IOException("Cannot create notification.");
                        try
                        {
                            LinuxTimeout(notification, 8000);
                            if (LinuxShow(notification, out var error) == 0)
                            {
                                if (error != 0) ErrorFree(error);
                                throw new IOException("Desktop notification service rejected delivery.");
                            }
                        }
                        finally { Unref(notification); }
                    }
                    completion.TrySetResult();
                }
                catch (Exception e) { completion.TrySetException(new IOException("Desktop notification delivery failed.", e)); }
            }
        }
        finally
        {
            if (initialized) { if (OperatingSystem.IsWindows()) WindowsShutdown(); else LinuxShutdown(); }
        }
    }
    public void Dispose() { queue.CompleteAdding(); if (worker.Join(TimeSpan.FromSeconds(3))) queue.Dispose(); }
    [DllImport("warframe_native", EntryPoint = "wfc_notify_init")] private static extern int WindowsInit();
    [DllImport("warframe_native", EntryPoint = "wfc_notify_show", CharSet = CharSet.Unicode)] private static extern int WindowsShow(string title, string body, string tag);
    [DllImport("warframe_native", EntryPoint = "wfc_notify_shutdown")] private static extern void WindowsShutdown();
    [DllImport("libnotify.so.4", EntryPoint = "notify_init")] private static extern int LinuxInit([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libnotify.so.4", EntryPoint = "notify_notification_new")] private static extern nint LinuxNew([MarshalAs(UnmanagedType.LPUTF8Str)] string title, [MarshalAs(UnmanagedType.LPUTF8Str)] string body, [MarshalAs(UnmanagedType.LPUTF8Str)] string icon);
    [DllImport("libnotify.so.4", EntryPoint = "notify_notification_set_timeout")] private static extern void LinuxTimeout(nint notification, int milliseconds);
    [DllImport("libnotify.so.4", EntryPoint = "notify_notification_show")] private static extern int LinuxShow(nint notification, out nint error);
    [DllImport("libnotify.so.4", EntryPoint = "notify_uninit")] private static extern void LinuxShutdown();
    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static extern void Unref(nint value);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] private static extern void ErrorFree(nint error);
}
