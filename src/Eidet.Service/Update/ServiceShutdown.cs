using System.Diagnostics;

namespace Eidet.Service.Update;

/// <summary>
/// Lets the updater ask a running <c>eidet serve</c> on Windows to stop itself, instead of having
/// <c>schtasks /end</c> terminate it.
///
/// Two reasons. A terminated service skips its shutdown (scheduler state, lock file, RavenDB
/// store). And the unattended install is a child of the service it is stopping: a service that
/// has already exited leaves nothing for <c>/end</c> to act on, so whether Task Scheduler takes
/// the updater down with a running task never comes into it (verified the same way in Parley).
///
/// The signal is a named event in the session-local namespace, keyed by the service's PID. Only
/// processes in the same logon session can open it, and those could already kill the service.
/// On macOS/Linux the service manager's stop sends SIGTERM, which <c>eidet serve</c> handles as a
/// graceful stop itself.
/// </summary>
internal static class ServiceShutdown
{
    private static string EventName(int pid) => $@"Local\Eidet-Shutdown-{pid}";

    /// <summary>Service side: runs <paramref name="onRequested"/> once when a stop is requested.</summary>
    public static IDisposable Listen(Action onRequested)
    {
        if (!OperatingSystem.IsWindows()) return new Registration(null, null);
        var ev = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(Environment.ProcessId));
        var wait = ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => onRequested(), null, Timeout.Infinite, executeOnlyOnce: true);
        return new Registration(ev, wait);
    }

    /// <summary>
    /// Updater side: asks the service with <paramref name="pid"/> to stop and waits for it to exit.
    /// False when it isn't listening (not Windows, or a version older than this) or didn't exit in
    /// time — the caller then falls back to the service manager.
    /// </summary>
    public static bool RequestStop(int pid, TimeSpan timeout)
    {
        if (!Signal(pid)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    internal static bool Signal(int pid)
    {
        if (!OperatingSystem.IsWindows() || !EventWaitHandle.TryOpenExisting(EventName(pid), out var ev))
            return false;
        using (ev) ev.Set();
        return true;
    }

    private sealed class Registration(EventWaitHandle? ev, RegisteredWaitHandle? wait) : IDisposable
    {
        public void Dispose()
        {
            if (ev is null) return;
            wait?.Unregister(ev);
            ev.Dispose();
        }
    }
}
