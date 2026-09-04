namespace Eidet.Core.Enrichment;

public enum EnrichmentPrompt
{
    OneLiner,
    Summary,
    ForesightHint,
    Entities,
    MergeObservations,
    DriftReview,
    Reflect,
}

public sealed record EnrichmentRequest(
    EnrichmentPrompt Kind,
    string Primary,
    IReadOnlyList<string>? Aux = null);

public interface IEnrichmentPort
{
    bool IsAvailable { get; }
    Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default);
    Task<bool> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// The model that answers this port right now, or null when the port has no opinion (test
    /// doubles, the null adapter). A chain reports whichever backend produced the last answer.
    /// </summary>
    string? ModelName => null;
}
