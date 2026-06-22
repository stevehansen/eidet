using System.Globalization;
using System.Text;

namespace Eidet.Core.Benchmark;

/// <summary>Aggregated (mean) metrics for one capability heading across its cases.</summary>
public sealed record CapabilityScore(
    AmaCapability Capability,
    int Cases,
    double RecallAtK,
    double Mrr,
    double NdcgAtK,
    double GoldSurvival);

/// <summary>
/// The benchmark outcome: the v2-fusion scorecard, the flat-baseline scorecard run over the SAME
/// dataset, and a deterministic <see cref="ToMarkdown"/> comparison. <see cref="ToMarkdown"/> is a
/// pure function of the two scorecards — no timestamps, no environment — so the committed
/// <c>docs/benchmark.md</c> can be asserted byte-equal in CI.
/// </summary>
public sealed record BenchmarkReport(
    IReadOnlyList<CapabilityScore> Fused,
    IReadOnlyList<CapabilityScore> Baseline)
{
    /// <summary>
    /// Capabilities a deterministic ranking harness cannot honestly score (they require an
    /// LLM in the loop) — rendered in the "not evaluated" section rather than fabricated.
    /// </summary>
    private static readonly AmaCapability[] NotEvaluated =
        [AmaCapability.CausalInference, AmaCapability.StateAbstraction];

    private const string DeferralReason =
        "requires LLM-in-loop; deferred to the SWE Context Bench epic (issue #36 follow-up)";

    /// <summary>
    /// Renders the scorecard markdown: a per-capability + overall comparison table (v2 vs baseline
    /// with deltas) and the not-evaluated note. Deterministic given the report — identical inputs
    /// always produce identical bytes.
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Eidet Retrieval Benchmark Scorecard");
        sb.AppendLine();
        sb.AppendLine(
            "This measures the part of recall Eidet **owns**: ranking, hybrid fusion, and type-budget");
        sb.AppendLine(
            "quality on a curated, adversarial in-repo dataset. Each gold case scripts per-arm raw");
        sb.AppendLine(
            "scores `(lex, vec)`; the runner feeds them through the **real** `RecallScoring.Fuse` +");
        sb.AppendLine(
            "`ApplyTypeBudgets` pipeline and compares the v2 fusion ranker against a **flat baseline**");
        sb.AppendLine(
            "(the pre-#33 scoring: lexical hits = 1.0, vector-only hits = 0.9, no normalization, no UCB,");
        sb.AppendLine(
            "no recency) over the same dataset. The lift is fusion recovering gold that flat scoring");
        sb.AppendLine("loses at the truncation boundary.");
        sb.AppendLine();
        sb.AppendLine(
            "It does **not** measure embedding quality (RavenDB owns embeddings, which can't run");
        sb.AppendLine(
            "deterministically in CI), and it is **not** the external SWE Context Bench leaderboard —");
        sb.AppendLine("that is a deferred epic. This is a deterministic, no-LLM, CI regression guard.");
        sb.AppendLine();

        sb.AppendLine("## v2 Fusion vs Flat Baseline");
        sb.AppendLine();
        sb.AppendLine("| Capability | Cases | Metric | v2 Fusion | Flat Baseline | Delta |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (var fused in Fused)
        {
            var baseline = FindBaseline(fused.Capability);
            AppendCapabilityRows(sb, fused, baseline);
        }

        AppendCapabilityRows(sb, Overall(Fused), Overall(Baseline), label: "**Overall**");

        sb.AppendLine();
        sb.AppendLine("## Capabilities not evaluated");
        sb.AppendLine();
        foreach (var capability in NotEvaluated)
            sb.AppendLine($"- **{capability}** — N/A — {DeferralReason}.");

        return sb.ToString();
    }

    private CapabilityScore FindBaseline(AmaCapability capability) =>
        Baseline.FirstOrDefault(b => b.Capability == capability)
        ?? new CapabilityScore(capability, 0, 0, 0, 0, 0);

    private static void AppendCapabilityRows(
        StringBuilder sb, CapabilityScore fused, CapabilityScore baseline, string? label = null)
    {
        var name = label ?? fused.Capability.ToString();
        AppendMetricRow(sb, name, fused.Cases, "Recall@k", fused.RecallAtK, baseline.RecallAtK, first: true);
        AppendMetricRow(sb, name, fused.Cases, "MRR", fused.Mrr, baseline.Mrr);
        AppendMetricRow(sb, name, fused.Cases, "nDCG@k", fused.NdcgAtK, baseline.NdcgAtK);
        AppendMetricRow(sb, name, fused.Cases, "Gold survival", fused.GoldSurvival, baseline.GoldSurvival);
    }

    private static void AppendMetricRow(
        StringBuilder sb, string name, int cases, string metric,
        double fused, double baseline, bool first = false)
    {
        // Only the first metric row of a capability carries the name + case count; the rest are
        // blank so the table reads as a grouped block.
        var nameCell = first ? name : "";
        var casesCell = first ? cases.ToString(CultureInfo.InvariantCulture) : "";
        sb.AppendLine(
            $"| {nameCell} | {casesCell} | {metric} | {Fmt(fused)} | {Fmt(baseline)} | {Delta(fused, baseline)} |");
    }

    private static CapabilityScore Overall(IReadOnlyList<CapabilityScore> scores)
    {
        // Case-weighted means so a capability with more cases counts proportionally. The Capability
        // field is a don't-care here — this aggregate is rendered under the "**Overall**" label, never
        // by its capability — so Recall is reused purely as a placeholder.
        var totalCases = scores.Sum(s => s.Cases);
        if (totalCases == 0)
            return new CapabilityScore(AmaCapability.Recall, 0, 0, 0, 0, 0);

        double Weighted(Func<CapabilityScore, double> pick) =>
            scores.Sum(s => pick(s) * s.Cases) / totalCases;

        return new CapabilityScore(
            AmaCapability.Recall, totalCases,
            Weighted(s => s.RecallAtK),
            Weighted(s => s.Mrr),
            Weighted(s => s.NdcgAtK),
            Weighted(s => s.GoldSurvival));
    }

    private static string Fmt(double value) =>
        value.ToString("F3", CultureInfo.InvariantCulture);

    private static string Delta(double fused, double baseline)
    {
        var delta = fused - baseline;
        var sign = delta >= 0 ? "+" : "";
        return sign + delta.ToString("F3", CultureInfo.InvariantCulture);
    }
}
