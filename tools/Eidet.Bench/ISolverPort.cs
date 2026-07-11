namespace Eidet.Bench;

/// <summary>
/// Everything the solver is allowed to see for one attempt. This record is the transcript cache
/// key: <see cref="Transcript"/> keys recorded results on the SHA-256 of its canonical JSON, so
/// any change to what the solver sees (including the recalled context) invalidates the recording.
/// </summary>
public sealed record SolveRequest(
    string InstanceId,
    string Repo,
    string BaseCommit,
    string ProblemStatement,
    IReadOnlyList<string> Context);

public sealed record SolveResult(string Patch, long TokensUsed);

/// <summary>
/// External touch #2: the LLM agent that attempts a patch. Mirrors the shape of
/// <c>Eidet.Core.Enrichment.IEnrichmentPort</c> — availability flag, one request/response
/// method, health check. Phase 0 ships only replay/recording adapters; a paid production
/// adapter is Phase 1.
/// </summary>
public interface ISolverPort
{
    bool IsAvailable { get; }
    Task<SolveResult> AttemptAsync(SolveRequest request, CancellationToken ct = default);
    Task<bool> CheckHealthAsync(CancellationToken ct = default);
}
