using System.Globalization;
using System.Text;
using Eidet.Core.Benchmark;

namespace Eidet.Bench;

/// <summary>
/// The outcome of one harness run. <see cref="ToMarkdown"/> is a pure function of the record
/// (no timestamps, no environment — <see cref="Runtime"/> is deliberately not rendered), so the
/// committed <c>docs/swe-context-bench.md</c> can be asserted byte-equal in CI, mirroring
/// <c>BenchmarkReport.ToMarkdown</c> / <c>docs/benchmark.md</c>.
/// </summary>
public sealed record SweBenchReport(
    string DatasetName,
    bool IsRealDataset,
    string BackendName,
    int RelatedTasks,
    int BaseTasks,
    int Resolved,
    long SolveTokens,
    TimeSpan Runtime,
    IReadOnlyList<CapabilityScore> Ama)
{
    public double ResolutionRate => BaseTasks == 0 ? 0 : (double)Resolved / BaseTasks;
    public long TokensPerResolved => Resolved == 0 ? 0 : SolveTokens / Resolved;

    /// <summary>
    /// The two capability rows this artifact owns; <c>docs/benchmark.md</c> keeps them N/A
    /// because they need an LLM in the loop.
    /// </summary>
    private static readonly AmaCapability[] LlmInLoopCapabilities =
        [AmaCapability.CausalInference, AmaCapability.StateAbstraction];

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SWE Context Bench — Eidet Memory-Backend Harness");
        sb.AppendLine();

        // The anti-misreporting guard, rendered: a fixture run can never read as a leaderboard figure.
        if (IsRealDataset)
        {
            sb.AppendLine($"> Recorded real run against `{DatasetName}`. The committed transcript re-derives this");
            sb.AppendLine("> report byte-for-byte on replay.");
        }
        else
        {
            sb.AppendLine("> **Fixture run — NOT a leaderboard number.** This report is rendered from the bundled");
            sb.AppendLine("> synthetic fixture and a replayed transcript; it exists to byte-guard the harness logic");
            sb.AppendLine("> in CI. A publishable SWE Context Bench figure requires a fresh recorded run against");
            sb.AppendLine($"> the real dataset ({LeaderboardGuard.DatasetUrl});");
            sb.AppendLine("> `eidet bench full` refuses to emit one from anything else.");
        }

        sb.AppendLine();
        sb.AppendLine("Methodology mirrors SWE-ContextBench (arXiv:2602.08316): related tasks are solved first");
        sb.AppendLine("and their trajectories are ingested into the memory backend; each base task then recalls");
        sb.AppendLine("context, the solver attempts a patch, and an execution oracle requires FAIL_TO_PASS +");
        sb.AppendLine("PASS_TO_PASS. Resolution rate and solver tokens count evaluated (base) attempts only.");
        sb.AppendLine();

        sb.AppendLine("## Run");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Dataset | {DatasetName} |");
        sb.AppendLine($"| Memory backend | {BackendName} |");
        sb.AppendLine($"| Related tasks (ingested) | {RelatedTasks.ToString(CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| Base tasks (evaluated) | {BaseTasks.ToString(CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| Resolved | {Resolved.ToString(CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| Resolution rate | {Fmt(ResolutionRate)} |");
        sb.AppendLine($"| Solver tokens per resolved | {(Resolved == 0 ? "n/a" : TokensPerResolved.ToString(CultureInfo.InvariantCulture))} |");
        sb.AppendLine();

        sb.AppendLine("## AMA capability rows");
        sb.AppendLine();
        sb.AppendLine("`CausalInference` and `StateAbstraction` complement the deterministic scorecard in");
        sb.AppendLine("[benchmark.md](benchmark.md) (which reports them N/A — they need an LLM in the loop)");
        sb.AppendLine("and are only ever populated here from a recorded real run.");
        sb.AppendLine();

        if (Ama.Count > 0)
        {
            sb.AppendLine("| Capability | Cases | Recall@k | MRR | nDCG@k | Gold survival |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var s in Ama)
                sb.AppendLine($"| {s.Capability} | {s.Cases.ToString(CultureInfo.InvariantCulture)} | {Fmt(s.RecallAtK)} | {Fmt(s.Mrr)} | {Fmt(s.NdcgAtK)} | {Fmt(s.GoldSurvival)} |");
            sb.AppendLine();
        }

        foreach (var capability in LlmInLoopCapabilities)
        {
            if (Ama.All(a => a.Capability != capability))
                sb.AppendLine($"- **{capability}** — N/A — pending a recorded real run (LLM-in-loop scorer, Phase 1 of issue #36).");
        }

        return sb.ToString();
    }

    private static string Fmt(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
