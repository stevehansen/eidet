namespace Eidet.Core.Tests.Services;

/// <summary>
/// Tests for FadeMem differential decay math.
/// Decay formula: importance * 2^(-(age/adjustedHalfLife)^shape)
/// </summary>
public class FadeMemDecayTests
{
    // Replicate the decay calculation from ConsolidationService.ApplyImportanceDecayAsync
    private static float ComputeDecay(float importance, float confidence, double daysSinceCreation,
        double halfLife, double shape)
    {
        var confidenceBoost = 1.0 + (confidence - 0.5) * 0.5;
        var adjustedHalfLife = halfLife * confidenceBoost;
        var normalizedAge = daysSinceCreation / adjustedHalfLife;
        var shapedAge = Math.Pow(normalizedAge, shape);
        var decayFactor = Math.Pow(2, -shapedAge);
        return Math.Max(0.05f, (float)(importance * decayFactor));
    }

    [Fact]
    public void Observation_DecaysFast()
    {
        // Observation: halfLife=30d, shape=1.2 (super-linear)
        var decayed = ComputeDecay(0.5f, 0.7f, 30, 30, 1.2);
        // At half-life with default confidence, should be roughly half
        Assert.InRange(decayed, 0.15f, 0.35f);
    }

    [Fact]
    public void Insight_DecaysSlower()
    {
        // Insight: halfLife=90d, shape=1.0 (linear exponential)
        var at30d = ComputeDecay(0.5f, 0.7f, 30, 90, 1.0);
        // At 30d, insight should retain much more than observation at 30d
        var obsAt30d = ComputeDecay(0.5f, 0.7f, 30, 30, 1.2);
        Assert.True(at30d > obsAt30d, $"Insight at 30d ({at30d}) should retain more than observation ({obsAt30d})");
    }

    [Fact]
    public void Heuristic_NearlyImmortal()
    {
        // Heuristic: halfLife=730d, shape=0.7 (sub-linear, nearly immortal)
        var at90d = ComputeDecay(0.5f, 0.7f, 90, 730, 0.7);
        Assert.True(at90d > 0.4f, $"Heuristic at 90d ({at90d}) should retain most of its importance");
    }

    [Fact]
    public void HighConfidence_SlowsDecay()
    {
        // High confidence (1.0) should give 1.25x half-life
        var highConf = ComputeDecay(0.5f, 1.0f, 60, 90, 1.0);
        var lowConf = ComputeDecay(0.5f, 0.0f, 60, 90, 1.0);
        Assert.True(highConf > lowConf, $"High confidence ({highConf}) should decay slower than low ({lowConf})");
    }

    [Fact]
    public void Floor_NeverBelowMinimum()
    {
        // Even after extreme time, floor is 0.05
        var ancient = ComputeDecay(0.5f, 0.5f, 10000, 30, 1.2);
        Assert.Equal(0.05f, ancient);
    }

    [Fact]
    public void ZeroDays_NoDecay()
    {
        var fresh = ComputeDecay(0.5f, 0.7f, 0, 30, 1.2);
        Assert.Equal(0.5f, fresh);
    }

    [Fact]
    public void TypeHierarchy_DecayOrdering()
    {
        // At 60 days, ordering should be: Observation < Insight < Procedure < Heuristic
        var obs = ComputeDecay(0.5f, 0.7f, 60, 30, 1.2);
        var ins = ComputeDecay(0.5f, 0.7f, 60, 90, 1.0);
        var proc = ComputeDecay(0.5f, 0.7f, 60, 365, 0.8);
        var heur = ComputeDecay(0.5f, 0.7f, 60, 730, 0.7);

        Assert.True(obs < ins, $"Observation ({obs}) should decay more than Insight ({ins})");
        Assert.True(ins < proc, $"Insight ({ins}) should decay more than Procedure ({proc})");
        Assert.True(proc < heur, $"Procedure ({proc}) should decay more than Heuristic ({heur})");
    }
}
