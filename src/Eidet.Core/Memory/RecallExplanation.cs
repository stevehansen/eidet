namespace Eidet.Core.Memory;

/// <summary>
/// Diagnostic breakdown of a recall fusion, produced by <c>MemoryService.ExplainRecallAsync</c>.
/// Surfaces the per-candidate component scores BEFORE type budgeting and the de-boost/staleness
/// pass, so callers can see exactly how each candidate was ranked. Never touches the recall cache.
/// </summary>
public sealed record RecallExplanation(IReadOnlyList<RecallExplanationRow> Rows, double AlphaUsed, int CandidatePool);

/// <summary>One candidate's fusion components: normalized arm scores plus recency + UCB contributions and the total.</summary>
public sealed record RecallExplanationRow(string Id, double Lex, double Vec, double Recency, double Ucb, double Fused);
