namespace Eidet.Core.Services;

/// <summary>
/// Enrichment service for LLM-assisted memory enhancement.
/// NullEnrichmentService provides zero-overhead no-op when disabled.
/// </summary>
public interface IEnrichmentService
{
    bool IsAvailable { get; }
    Task<string?> GenerateOneLinerAsync(string content, CancellationToken ct = default);
    Task<string?> GenerateSummaryAsync(string content, CancellationToken ct = default);
    Task<string?> GenerateForesightHintAsync(string content, CancellationToken ct = default);
    Task<List<string>> ExtractEntitiesAsync(string content, CancellationToken ct = default);
    Task<string?> MergeObservationsAsync(List<string> observations, CancellationToken ct = default);
    Task<string?> DetectConflictAsync(string newContent, string existingContent, CancellationToken ct = default);
    Task<bool> CheckHealthAsync(CancellationToken ct = default);
}
