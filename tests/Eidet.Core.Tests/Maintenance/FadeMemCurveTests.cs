using Eidet.Core.Domain;
using Eidet.Core.Maintenance;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Tests for FadeMem differential decay math.
/// Decay formula: importance * 2^(-(age/adjustedHalfLife)^shape), floored at 0.05.
/// </summary>
public class FadeMemCurveTests
{
    [Fact]
    public void Observation_DecaysFast()
    {
        var decayed = FadeMemCurve.Decay(0.5f, 0.7f, 30, MemoryType.Observation);
        Assert.InRange(decayed, 0.15f, 0.35f);
    }

    [Fact]
    public void Insight_DecaysSlowerThanObservation_AtSameAge()
    {
        var insight = FadeMemCurve.Decay(0.5f, 0.7f, 30, MemoryType.Insight);
        var observation = FadeMemCurve.Decay(0.5f, 0.7f, 30, MemoryType.Observation);
        Assert.True(insight > observation, $"Insight ({insight}) should retain more than observation ({observation})");
    }

    [Fact]
    public void Heuristic_NearlyImmortal()
    {
        var decayed = FadeMemCurve.Decay(0.5f, 0.7f, 90, MemoryType.Heuristic);
        Assert.True(decayed > 0.4f, $"Heuristic at 90d ({decayed}) should retain most of its importance");
    }

    [Fact]
    public void HighConfidence_SlowsDecay()
    {
        var highConf = FadeMemCurve.Decay(0.5f, 1.0f, 60, MemoryType.Insight);
        var lowConf = FadeMemCurve.Decay(0.5f, 0.0f, 60, MemoryType.Insight);
        Assert.True(highConf > lowConf, $"High confidence ({highConf}) should decay slower than low ({lowConf})");
    }

    [Fact]
    public void Floor_NeverBelowMinimum()
    {
        var ancient = FadeMemCurve.Decay(0.5f, 0.5f, 10000, MemoryType.Observation);
        Assert.Equal(FadeMemCurve.Floor, ancient);
    }

    [Fact]
    public void ZeroDays_NoDecay()
    {
        var fresh = FadeMemCurve.Decay(0.5f, 0.7f, 0, MemoryType.Observation);
        Assert.Equal(0.5f, fresh);
    }

    [Fact]
    public void TypeHierarchy_DecayOrdering()
    {
        var obs = FadeMemCurve.Decay(0.5f, 0.7f, 60, MemoryType.Observation);
        var ins = FadeMemCurve.Decay(0.5f, 0.7f, 60, MemoryType.Insight);
        var proc = FadeMemCurve.Decay(0.5f, 0.7f, 60, MemoryType.Procedure);
        var heur = FadeMemCurve.Decay(0.5f, 0.7f, 60, MemoryType.Heuristic);

        Assert.True(obs < ins, $"Observation ({obs}) should decay more than Insight ({ins})");
        Assert.True(ins < proc, $"Insight ({ins}) should decay more than Procedure ({proc})");
        Assert.True(proc < heur, $"Procedure ({proc}) should decay more than Heuristic ({heur})");
    }

    [Fact]
    public void Defaults_CoversAllTypes()
    {
        foreach (var type in Enum.GetValues<MemoryType>())
            Assert.True(FadeMemCurve.Defaults.ContainsKey(type), $"Missing curve for {type}");
    }
}
