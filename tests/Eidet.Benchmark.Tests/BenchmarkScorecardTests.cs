using Eidet.Core.Benchmark;

namespace Eidet.Benchmark.Tests;

/// <summary>
/// The CI regression guard for the retrieval scorecard: v2 fusion must strictly beat the flat baseline
/// on every metric of the adversarial Recall dataset, the absolute Fused numbers must clear
/// conservative floors, fusion must NOT score a perfect 1.0 (the dataset includes cases it cannot
/// solve, so the headline can't read as a self-drawn curve), every metric must be finite and in
/// [0, 1], and the run must be deterministic.
/// </summary>
public class BenchmarkScorecardTests
{
    private static BenchmarkReport RunRecall() =>
        BenchmarkRunner.Run(GoldDataset.Cases, GoldDataset.Now);

    private static CapabilityScore Recall(IReadOnlyList<CapabilityScore> scores) =>
        scores.Single(s => s.Capability == AmaCapability.Recall);

    [Fact]
    public void Fusion_StrictlyBeatsFlatBaseline_OnEveryMetric()
    {
        var report = RunRecall();
        var fused = Recall(report.Fused);
        var baseline = Recall(report.Baseline);

        Assert.True(fused.RecallAtK > baseline.RecallAtK,
            $"fused recall@k ({fused.RecallAtK}) must exceed baseline ({baseline.RecallAtK})");
        Assert.True(fused.GoldSurvival > baseline.GoldSurvival,
            $"fused gold survival ({fused.GoldSurvival}) must exceed baseline ({baseline.GoldSurvival})");
        Assert.True(fused.Mrr > baseline.Mrr,
            $"fused MRR ({fused.Mrr}) must exceed baseline ({baseline.Mrr})");
        Assert.True(fused.NdcgAtK > baseline.NdcgAtK,
            $"fused nDCG@k ({fused.NdcgAtK}) must exceed baseline ({baseline.NdcgAtK})");
    }

    [Fact]
    public void FusedRecall_ClearsConservativeFloors_ButIsNotPerfect()
    {
        var fused = Recall(RunRecall().Fused);

        // Conservative floors below the current Fused numbers — a guard against ranking regressions,
        // not a tight pin. (Current: recall 0.833, survival 0.792, MRR 0.889, nDCG 0.833.)
        Assert.True(fused.RecallAtK >= 0.75, $"fused recall@k was {fused.RecallAtK}");
        Assert.True(fused.GoldSurvival >= 0.70, $"fused gold survival was {fused.GoldSurvival}");
        Assert.True(fused.Mrr >= 0.80, $"fused MRR was {fused.Mrr}");
        Assert.True(fused.NdcgAtK >= 0.75, $"fused nDCG@k was {fused.NdcgAtK}");

        // The dataset MUST contain cases fusion cannot fully solve — otherwise a perfect score would
        // read as grading on a self-drawn curve. This locks in the honesty of the headline number.
        Assert.True(fused.RecallAtK < 1.0,
            "dataset should include cases fusion does not fully solve (no perfect-1.0 curve)");
    }

    [Fact]
    public void AllMetrics_AreFinite_AndInUnitInterval()
    {
        var report = RunRecall();
        foreach (var score in report.Fused.Concat(report.Baseline))
        {
            AssertUnit(score.RecallAtK);
            AssertUnit(score.Mrr);
            AssertUnit(score.NdcgAtK);
            AssertUnit(score.GoldSurvival);
        }
    }

    [Fact]
    public void Run_IsDeterministic_AcrossRepeatedCalls()
    {
        var a = RunRecall();
        var b = RunRecall();
        Assert.Equal(a.ToMarkdown(), b.ToMarkdown());
    }

    [Fact]
    public async Task FullScorecard_IncludingStateUpdating_RendersDeterministically()
    {
        var first = (await ScorecardBuilder.BuildAsync()).ToMarkdown();
        var second = (await ScorecardBuilder.BuildAsync()).ToMarkdown();
        Assert.Equal(first, second);

        // StateUpdating (the FAMA guard) must pass — it is the StateUpdating capability line.
        Assert.True(await FamaForgetTests.StateUpdatingPasses());
    }

    private static void AssertUnit(double value)
    {
        Assert.False(double.IsNaN(value), "metric was NaN");
        Assert.False(double.IsInfinity(value), "metric was Infinity");
        Assert.InRange(value, 0.0, 1.0);
    }
}
