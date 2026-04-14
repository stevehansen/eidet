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
