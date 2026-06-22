namespace Eidet.Core.Benchmark;

/// <summary>
/// The four AMA-Bench capability headings the scorecard reports under. A deterministic, no-LLM
/// ranking harness genuinely exercises only <see cref="Recall"/> and <see cref="StateUpdating"/>;
/// <see cref="CausalInference"/> and <see cref="StateAbstraction"/> require an LLM in the loop and
/// are reported as not-evaluated (see <c>BenchmarkReport.ToMarkdown</c>).
/// </summary>
public enum AmaCapability
{
    Recall,
    CausalInference,
    StateUpdating,
    StateAbstraction,
}
