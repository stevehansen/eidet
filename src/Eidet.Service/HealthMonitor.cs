using Eidet.Core.Configuration;
using Eidet.Core.Storage;

namespace Eidet.Service;

/// <summary>
/// Background health monitor that periodically checks dependency health (RavenDB and the
/// enrichment backend) and raises events when status changes. Used by ServeCommand to print
/// live status updates. The enrichment probe path is provider-aware so it works against both
/// Ollama-native and OpenAI-compatible servers.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    public record HealthState(bool RavenDbHealthy, bool EnrichmentHealthy);

    /// <summary>The lightweight liveness endpoint to probe for a given enrichment provider.</summary>
    internal static string ProbePathFor(EnrichmentProvider provider) =>
        provider == EnrichmentProvider.OpenAiCompatible ? "/v1/models" : "/api/tags";

    /// <summary>
    /// Fired when a dependency's health status changes.
    /// Parameters: (componentName, isHealthy, detail).
    /// </summary>
    public event Action<string, bool, string>? OnStatusChanged;

    private readonly IEidetStore _store;
    private readonly HttpClient? _enrichmentHttp;
    private readonly bool _enrichmentEnabled;
    private readonly string _enrichmentModel;
    private readonly string _enrichmentUrl;
    private readonly string _probePath;
    private readonly string _ravenUrl;
    private readonly Timer _timer;
    private readonly CancellationToken _ct;

    private bool _ravenHealthy = true; // assume healthy at start (we just connected)
    private bool _enrichmentHealthy;
    private bool _disposed;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    public HealthMonitor(
        IEidetStore store,
        bool enrichmentEnabled,
        EnrichmentProvider provider,
        string enrichmentModel,
        string enrichmentUrl,
        string ravenUrl,
        bool initialEnrichmentHealthy,
        CancellationToken ct)
    {
        _store = store;
        _enrichmentEnabled = enrichmentEnabled;
        _enrichmentModel = enrichmentModel;
        _enrichmentUrl = enrichmentUrl;
        _probePath = ProbePathFor(provider);
        _ravenUrl = ravenUrl;
        _enrichmentHealthy = initialEnrichmentHealthy;
        _ct = ct;
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);

        if (enrichmentEnabled)
        {
            _enrichmentHttp = new HttpClient
            {
                BaseAddress = new Uri(enrichmentUrl.TrimEnd('/')),
                Timeout = TimeSpan.FromSeconds(5),
            };
        }
    }

    public HealthState CurrentState => new(_ravenHealthy, _enrichmentHealthy);

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

            if (_enrichmentEnabled)
                await CheckEnrichmentAsync();
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

    private async Task CheckEnrichmentAsync()
    {
        // Direct lightweight check — bypasses the adapter's 5-minute health cache
        // so we can detect status changes within 30 seconds.
        bool healthy;
        try
        {
            using var response = await _enrichmentHttp!.GetAsync(_probePath, _ct);
            healthy = response.IsSuccessStatusCode;
        }
        catch
        {
            healthy = false;
        }

        if (healthy != _enrichmentHealthy)
        {
            _enrichmentHealthy = healthy;
            var detail = healthy
                ? $"Connected ({_enrichmentModel} @ {_enrichmentUrl})"
                : $"Unavailable ({_enrichmentModel} @ {_enrichmentUrl})";
            OnStatusChanged?.Invoke("Enrichment", healthy, detail);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _enrichmentHttp?.Dispose();
    }
}
