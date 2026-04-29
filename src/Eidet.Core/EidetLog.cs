using Eidet.Core.Configuration;

namespace Eidet.Core;

/// <summary>
/// Simple file logger for the Eidet service. Writes timestamped entries to
/// {configDir}/logs/eidet.log with automatic size rotation.
/// Thread-safe via a lock; fire-and-forget safe (never throws).
/// </summary>
public static class EidetLog
{
    private static readonly object Lock = new();
    private static string? _logPath;
    private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MB

    private static string GetLogPath()
    {
        if (_logPath != null) return _logPath;
        var logDir = Path.Combine(ConfigManager.GetConfigDir(), "logs");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "eidet.log");
        return _logPath;
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message) => Write("ERR", message);
    public static void Error(string message, Exception ex) => Write("ERR", $"{message}: {ex}");

    public static string LogPath => GetLogPath();

    private static int _crashHandlersInstalled;

    /// <summary>
    /// Hooks AppDomain.UnhandledException, TaskScheduler.UnobservedTaskException
    /// and ProcessExit to ensure crashes leave a trace in eidet.log. Idempotent.
    /// </summary>
    public static void InstallCrashHandlers(string processLabel)
    {
        if (Interlocked.Exchange(ref _crashHandlersInstalled, 1) == 1)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var prefix = e.IsTerminating
                ? $"[{processLabel}] Unhandled exception (terminating)"
                : $"[{processLabel}] Unhandled exception";
            if (ex != null)
                Error(prefix, ex);
            else
                Error($"{prefix}: {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error($"[{processLabel}] Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Info($"[{processLabel}] Process exiting (PID {Environment.ProcessId})");
        };
    }

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Lock)
            {
                var path = GetLogPath();
                RotateIfNeeded(path);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Never fail the caller
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length < MaxLogSizeBytes) return;

            var rotated = path + ".1";
            if (File.Exists(rotated)) File.Delete(rotated);
            File.Move(path, rotated);
        }
        catch { }
    }
}
