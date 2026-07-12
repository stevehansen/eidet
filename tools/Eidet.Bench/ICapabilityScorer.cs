using Eidet.Core.Benchmark;

namespace Eidet.Bench;

/// <summary>
/// Fills one AMA capability row (<c>CausalInference</c> / <c>StateAbstraction</c> — the two rows
/// <c>docs/benchmark.md</c> reports as N/A) from a run's solve outcomes. Concrete LLM-judge
/// scorers are Phase 1; their scores land only in <c>docs/swe-context-bench.md</c>, never in the
/// deterministic scorecard.
/// </summary>
/// <remarks>
/// Deviation from the design sketch (<c>Score(AmaCapability, outcomes)</c>): the scorer declares
/// the capability it serves, otherwise the harness has no way to know which rows a scorer fills.
/// </remarks>
public interface ICapabilityScorer
{
    AmaCapability Capability { get; }
    CapabilityScore Score(IReadOnlyList<SolveOutcome> outcomes);
}
