namespace Eidet.Core.Enrichment;

/// <summary>
/// An ordered chain of backends behind one port. Every call goes to the first backend whose health
/// probe passes and that returns text; one that is offline, rejects the request, or fails mid-call
/// hands the request to the next. Built for a private network model that may not always be
/// reachable in front of a local one that always is — callers see a single port and never learn
/// which answered, except through <see cref="ModelName"/>.
/// </summary>
/// <remarks>
/// Each backend keeps its own health cache, so a primary that is down costs one probe per five
/// minutes, not one per call. A backend that answers <c>null</c> for a valid prompt (rare: empty
/// completion) also falls through, which costs a second model call rather than a lost enrichment.
/// </remarks>
internal sealed class FallbackEnrichmentAdapter : IEnrichmentPort, IDisposable
{
    private readonly IReadOnlyList<IEnrichmentPort> _backends;
    private IEnrichmentPort _lastAnswered;

    public FallbackEnrichmentAdapter(IReadOnlyList<IEnrichmentPort> backends)
    {
        if (backends.Count == 0) throw new ArgumentException("A fallback chain needs at least one backend.", nameof(backends));
        _backends = backends;
        _lastAnswered = backends[0];
    }

    public bool IsAvailable => _backends.Any(b => b.IsAvailable);

    /// <summary>The model behind the most recent answer; the primary's until something has answered.</summary>
    public string? ModelName => _lastAnswered.ModelName;

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        foreach (var backend in _backends)
            if (await backend.CheckHealthAsync(ct)) return true;
        return false;
    }

    public async Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        foreach (var backend in _backends)
        {
            if (!await backend.CheckHealthAsync(ct)) continue;
            var text = await backend.CompleteAsync(request, ct);
            if (text is null) continue;
            _lastAnswered = backend;
            return text;
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var backend in _backends)
            if (backend is IDisposable d) d.Dispose();
    }
}
