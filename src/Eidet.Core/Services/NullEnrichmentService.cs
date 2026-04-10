namespace Eidet.Core.Services;

/// <summary>
/// No-op enrichment service. Zero overhead when Ollama is disabled.
/// </summary>
public sealed class NullEnrichmentService : IEnrichmentService
{
    public static readonly NullEnrichmentService Instance = new();

    public bool IsAvailable => false;
    public Task<string?> GenerateOneLinerAsync(string content, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> GenerateSummaryAsync(string content, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> GenerateForesightHintAsync(string content, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<List<string>> ExtractEntitiesAsync(string content, CancellationToken ct = default) => Task.FromResult(new List<string>());
    public Task<string?> MergeObservationsAsync(List<string> observations, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> DetectConflictAsync(string newContent, string existingContent, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(false);
}
