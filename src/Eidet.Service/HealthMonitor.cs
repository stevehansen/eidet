using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.Storage;

namespace Eidet.Service;

/// <summary>
/// Background health monitor that periodically checks dependency health (RavenDB and the
/// enrichment backends) and raises events when status changes. Used by ServeCommand to print
/// live status updates. Enrichment is healthy while any backend in the chain answers its
/// provider-specific probe, and the event detail names the one that did — so a primary that
/// drops out and a fallback that takes over are both visible in the service log.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    public record HealthState(bool RavenDbHealthy, bool EnrichmentHealthy);

    /// <summary>
    /// Fired when a dependency's health status changes.
    /// Parameters: (componentName, isHealthy, detail).
    /// </summary>
    public event Action<string, bool, string>? OnStatusChanged;

    private readonly IEidetStore _store;
    private EnrichmentConfig _enrichment;
    private List<(HttpClient Http, EnrichmentBackendConfig Backend)> _probes = [];
    private EnrichmentBackendConfig? _answering;
    private readonly string _ravenUrl;
    private readonly Timer _timer;
    private readonly CancellationToken _ct;

    private bool _ravenHealthy = true; // assume healthy at start (we just connected)
    private bool _enrichmentHealthy;
    private bool _disposed;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public HealthMonitor(
        IEidetStore store,
        EnrichmentConfig enrichment,
        string ravenUrl,
        bool initialEnrichmentHealthy,
        CancellationToken ct)
    {
        _store = store;
        _enrichment = enrichment;
        _ravenUrl = ravenUrl;
        _enrichmentHealthy = initialEnrichmentHealthy;
        _ct = ct;
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        Target(enrichment);
    }

    public HealthState CurrentState => new(_ravenHealthy, _enrichmentHealthy);

    /// <summary>
    /// Points the probes at the reloaded chain and announces the new target via
    /// <see cref="OnStatusChanged"/>. A probe already in flight on an old client lands in the
    /// tick's catch and is dropped.
    /// </summary>
    public void ReconfigureEnrichment(EnrichmentConfig fresh, bool healthy)
    {
        var old = _probes;
        Target(fresh);
        _enrichmentHealthy = healthy;
        _answering = null;
        foreach (var (http, _) in old) http.Dispose();

        var detail = !fresh.Enabled ? "Disabled (config reload)"
            : healthy ? $"Reloaded — Connected ({DescribeChain(fresh)})"
            : $"Reloaded — Unavailable ({DescribeChain(fresh)})";
        OnStatusChanged?.Invoke("Enrichment", !fresh.Enabled || healthy, detail);
    }

    private void Target(EnrichmentConfig enrichment)
    {
        _enrichment = enrichment;
        _probes = enrichment.Enabled
            ? enrichment.Backends.Select(b => (EnrichmentHttp.CreateClient(b, ProbeTimeout), b)).ToList()
            : [];
    }

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

            if (_enrichment.Enabled)
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
        // Direct lightweight probes — bypass the adapters' 5-minute health cache so a status
        // change shows within 30 seconds. First backend to answer wins, in chain order.
        var probes = _probes;
        EnrichmentBackendConfig? answering = null;
        foreach (var (http, backend) in probes)
        {
            try
            {
                using var response = await http.GetAsync(EnrichmentHttp.ProbePath(backend.Provider), _ct);
                if (response.IsSuccessStatusCode)
                {
                    answering = backend;
                    break;
                }
            }
            catch
            {
                // unreachable — try the next one
            }
        }

        var healthy = answering is not null;
        if (healthy == _enrichmentHealthy && ReferenceEquals(answering, _answering))
            return;

        _enrichmentHealthy = healthy;
        _answering = answering;
        var detail = answering is null
            ? $"Unavailable ({DescribeChain(_enrichment)})"
            : ReferenceEquals(answering, probes[0].Backend)
                ? $"Connected ({DescribeBackend(answering)})"
                : $"Connected via fallback ({DescribeBackend(answering)})";
        OnStatusChanged?.Invoke("Enrichment", healthy, detail);
    }

    private static string DescribeBackend(EnrichmentBackendConfig b) => $"{b.Model} @ {b.Url}";

    private static string DescribeChain(EnrichmentConfig c) => c.Fallbacks.Count switch
    {
        0 => DescribeBackend(c),
        1 => $"{DescribeBackend(c)}, +1 fallback",
        var n => $"{DescribeBackend(c)}, +{n} fallbacks",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        foreach (var (http, _) in _probes) http.Dispose();
    }
}
