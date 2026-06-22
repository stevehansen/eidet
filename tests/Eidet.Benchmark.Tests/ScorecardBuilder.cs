using Eidet.Core.Benchmark;

namespace Eidet.Benchmark.Tests;

/// <summary>
/// Assembles the canonical <see cref="BenchmarkReport"/> the committed scorecard renders from: the
/// Recall capability from <see cref="GoldDataset"/> run through <see cref="BenchmarkRunner"/>, plus a
/// StateUpdating row carrying the FAMA forget/supersede pass-rate. StateUpdating correctness is
/// ranker-independent (forget/supersede suppression is not a fusion feature), so its Fused and
/// Baseline values are equal — the row reports the behavioral guarantee, not a fusion lift.
/// </summary>
public static class ScorecardBuilder
{
    /// <summary>Builds the report at <see cref="GoldDataset.Now"/>; deterministic given the dataset + FAMA result.</summary>
    public static async Task<BenchmarkReport> BuildAsync()
    {
        var recall = BenchmarkRunner.Run(GoldDataset.Cases, GoldDataset.Now);
        var famaPass = await FamaForgetTests.StateUpdatingPasses() ? 1.0 : 0.0;
        var stateUpdating = StateUpdatingRow(famaPass);

        return new BenchmarkReport(
            Fused: Append(recall.Fused, stateUpdating),
            Baseline: Append(recall.Baseline, stateUpdating));
    }

    private static CapabilityScore StateUpdatingRow(double passRate) =>
        // One aggregate "case" — the combined FAMA guard. Every metric reflects the same pass-rate
        // so the row reads as a clean pass/fail across all columns.
        new(AmaCapability.StateUpdating, Cases: 1,
            RecallAtK: passRate, Mrr: passRate, NdcgAtK: passRate, GoldSurvival: passRate);

    private static IReadOnlyList<CapabilityScore> Append(
        IReadOnlyList<CapabilityScore> scores, CapabilityScore extra) =>
        scores.Append(extra).OrderBy(s => s.Capability).ToList();
}
