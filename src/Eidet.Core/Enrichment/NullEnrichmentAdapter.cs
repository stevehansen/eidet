namespace Eidet.Core.Enrichment;

internal sealed class NullEnrichmentAdapter : IEnrichmentPort
{
    public bool IsAvailable => false;
    public Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(false);
}
