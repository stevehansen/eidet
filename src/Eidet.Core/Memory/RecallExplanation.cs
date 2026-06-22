namespace Eidet.Core.Memory;

/// <summary>
/// Diagnostic breakdown of a recall fusion, produced by <c>MemoryService.ExplainRecallAsync</c>.
/// Surfaces the per-candidate component scores BEFORE type budgeting and the de-boost/staleness
/// pass, so callers can see exactly how each candidate was ranked. Never touches the recall cache.
/// </summary>
public sealed record RecallExplanation(IReadOnlyList<RecallExplanationRow> Rows, double AlphaUsed, int CandidatePool);

/// <summary>
/// One candidate's fusion components: normalized arm scores plus recency + UCB contributions and the
/// total. <see cref="Trust"/> is the derived trust factor and <see cref="Gated"/> = Fused·Trust is the
/// production-recall score after trust gating, so the diagnostic mirrors what live recall actually ranks.
/// </summary>
public sealed record RecallExplanationRow(
    string Id, double Lex, double Vec, double Recency, double Ucb, double Fused, double Trust, double Gated);
