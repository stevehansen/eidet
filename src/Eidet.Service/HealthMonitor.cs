using Eidet.Core.Storage;

namespace Eidet.Service;

/// <summary>
/// Background health monitor that periodically checks dependency health (RavenDB, Ollama)
/// and raises events when status changes. Used by ServeCommand to print live status updates.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    public record HealthState(bool RavenDbHealthy, bool OllamaHealthy);

    /// <summary>
    /// Fired when a dependency's health status changes.
    /// Parameters: (componentName, isHealthy, detail).
    /// </summary>
    public event Action<string, bool, string>? OnStatusChanged;

    private readonly IEidetStore _store;
    private readonly HttpClient? _ollamaHttp;
    private readonly bool _ollamaEnabled;
    private readonly string _ollamaModel;
    private readonly string _ollamaUrl;
    private readonly string _ravenUrl;
    private readonly Timer _timer;
    private readonly CancellationToken _ct;

    private bool _ravenHealthy = true; // assume healthy at start (we just connected)
    private bool _ollamaHealthy;
    private bool _disposed;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    public HealthMonitor(
        IEidetStore store,
        bool ollamaEnabled,
        string ollamaModel,
        string ollamaUrl,
        string ravenUrl,
        bool initialOllamaHealthy,
        CancellationToken ct)
    {
        _store = store;
        _ollamaEnabled = ollamaEnabled;
        _ollamaModel = ollamaModel;
        _ollamaUrl = ollamaUrl;
        _ravenUrl = ravenUrl;
        _ollamaHealthy = initialOllamaHealthy;
        _ct = ct;
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);

        if (ollamaEnabled)
        {
            _ollamaHttp = new HttpClient
            {
                BaseAddress = new Uri(ollamaUrl.TrimEnd('/')),
                Timeout = TimeSpan.FromSeconds(5),
            };
        }
    }

    public HealthState CurrentState => new(_ravenHealthy, _ollamaHealthy);

    public void Start()
    {
        _timer.Change(InitialDelay, CheckInterval);
    }

    private async void OnTick(object? state)
    {
        if (_ct.IsCancellationRequested || _disposed) return;

        try
        {
            await CheckRavenDbAsync();

            if (_ollamaEnabled)
                await CheckOllamaAsync();
        }
        catch
        {
            // Health check itself should never crash the service
        }
    }

    private async Task CheckRavenDbAsync()
    {
        var healthy = await _store.TestConnectionAsync(_ct);

        if (healthy != _ravenHealthy)
        {
            _ravenHealthy = healthy;
            var detail = healthy ? $"Connected ({_ravenUrl})" : $"Unreachable ({_ravenUrl})";
            OnStatusChanged?.Invoke("RavenDB", healthy, detail);
        }
    }

    private async Task CheckOllamaAsync()
    {
        // Direct lightweight check — bypasses the Ollama adapter's 5-minute health cache
        // so we can detect status changes within 30 seconds.
        bool healthy;
        try
        {
            var response = await _ollamaHttp!.GetAsync("/api/tags", _ct);
            healthy = response.IsSuccessStatusCode;
        }
        catch
        {
            healthy = false;
        }

        if (healthy != _ollamaHealthy)
        {
            _ollamaHealthy = healthy;
            var detail = healthy
                ? $"Connected ({_ollamaModel} @ {_ollamaUrl})"
                : $"Unavailable ({_ollamaModel} @ {_ollamaUrl})";
            OnStatusChanged?.Invoke("Ollama", healthy, detail);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _ollamaHttp?.Dispose();
    }
}
