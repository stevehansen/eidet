namespace Eidet.Core.Enrichment;

/// <summary>
/// Caches the result of a backend health probe for 5 minutes so enrichment doesn't
/// hammer the server on every memory. Shared by the Ollama-native and OpenAI-compatible
/// adapters, which differ only in the probe endpoint they pass to <see cref="CheckAsync"/>.
/// </summary>
internal sealed class EnrichmentHealthCache
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private bool? _lastHealthy;
    private DateTime _lastCheck = DateTime.MinValue;

    public EnrichmentHealthCache(HttpClient http) => _http = http;

    // Optimistic once the cache goes stale, regardless of the last result: a stale
    // unhealthy verdict must not pin enrichment off forever — letting IsAvailable return
    // true after CacheDuration lets the next call re-probe and recover when the backend
    // comes back (the actual probe still happens in CheckAsync).
    public bool IsAvailable => _lastHealthy == true ||
        DateTime.UtcNow - _lastCheck > CacheDuration;

    public async Task<bool> CheckAsync(string probePath, CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastCheck < CacheDuration && _lastHealthy.HasValue)
            return _lastHealthy.Value;

        try
        {
            var response = await _http.GetAsync(probePath, ct);
            _lastHealthy = response.IsSuccessStatusCode;
        }
        catch
        {
            _lastHealthy = false;
        }

        _lastCheck = DateTime.UtcNow;
        return _lastHealthy.Value;
    }
}
