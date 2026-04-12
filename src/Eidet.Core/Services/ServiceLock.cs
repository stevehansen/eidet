using System.Diagnostics;
using System.Text.Json;
using Eidet.Core.Configuration;

namespace Eidet.Core.Services;

/// <summary>
/// Manages a lock file for the running Eidet service. The lock file contains PID, port,
/// and bind address so other commands (status, doctor) can detect the running service.
/// </summary>
public sealed class ServiceLock : IDisposable
{
    private static readonly string LockPath = Path.Combine(ConfigManager.GetConfigDir(), "eidet.lock");

    private FileStream? _lockStream;

    public record ServiceInfo(int Pid, int Port, string BindAddress, DateTimeOffset StartedAt);

    /// <summary>
    /// Try to acquire the service lock. Returns false if another instance is already running.
    /// </summary>
    public bool TryAcquire(int port, string bindAddress, out ServiceInfo? existingService)
    {
        existingService = null;

        // Check for stale lock first
        var existing = Read();
        if (existing != null)
        {
            try
            {
                var process = Process.GetProcessById(existing.Pid);
                // Process exists — is it actually eidet?
                if (!process.HasExited)
                {
                    existingService = existing;
                    return false;
                }
            }
            catch (ArgumentException)
            {
                // Process doesn't exist — stale lock, we can overwrite
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(LockPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Open with exclusive write lock
            _lockStream = new FileStream(LockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

            var info = new ServiceInfo(
                Environment.ProcessId,
                port,
                bindAddress,
                DateTimeOffset.UtcNow);

            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            _lockStream.Write(bytes);
            _lockStream.Flush();

            return true;
        }
        catch (IOException)
        {
            // Another process holds the lock
            existingService = existing;
            return false;
        }
    }

    /// <summary>
    /// Read the current lock file (shared read). Returns null if no lock exists or it's unreadable.
    /// </summary>
    public static ServiceInfo? Read()
    {
        try
        {
            if (!File.Exists(LockPath))
                return null;

            using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<ServiceInfo>(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if the service described in the lock file is actually running.
    /// </summary>
    public static bool IsServiceRunning(out ServiceInfo? info)
    {
        info = Read();
        if (info == null)
            return false;

        try
        {
            var process = Process.GetProcessById(info.Pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Check if the service is running and responding to health checks.
    /// </summary>
    public static async Task<(bool Running, bool Healthy, ServiceInfo? Info)> CheckHealthAsync(
        CancellationToken ct = default)
    {
        if (!IsServiceRunning(out var info))
            return (false, false, info);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"http://{info!.BindAddress}:{info.Port}/api/health";
            var response = await http.GetAsync(url, ct);
            return (true, response.IsSuccessStatusCode, info);
        }
        catch
        {
            return (true, false, info);
        }
    }

    public void Dispose()
    {
        _lockStream?.Dispose();
        _lockStream = null;

        // Clean up the lock file
        try { File.Delete(LockPath); } catch { }
    }
}
