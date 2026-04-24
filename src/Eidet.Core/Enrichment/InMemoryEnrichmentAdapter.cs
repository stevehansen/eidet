namespace Eidet.Core.Enrichment;

/// <summary>
/// Test-only adapter. Callers seed responses per prompt kind; the adapter returns
/// canned output, making EnrichmentService callers deterministic without touching HTTP.
/// </summary>
public sealed class InMemoryEnrichmentAdapter : IEnrichmentPort
{
    private readonly Dictionary<EnrichmentPrompt, Func<EnrichmentRequest, string?>> _responders = new();

    public bool IsAvailable { get; set; } = true;

    public InMemoryEnrichmentAdapter SetResponse(EnrichmentPrompt kind, string? response)
    {
        _responders[kind] = _ => response;
        return this;
    }

    public InMemoryEnrichmentAdapter SetResponder(EnrichmentPrompt kind, Func<EnrichmentRequest, string?> responder)
    {
        _responders[kind] = responder;
        return this;
    }

    public Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        if (!IsAvailable) return Task.FromResult<string?>(null);
        return Task.FromResult(_responders.TryGetValue(request.Kind, out var fn) ? fn(request) : null);
    }

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(IsAvailable);
}
