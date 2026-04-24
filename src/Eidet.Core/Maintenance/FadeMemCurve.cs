using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance;

/// <summary>
/// FadeMem differential decay curve: importance * 2^(-(age/adjustedHalfLife)^shape).
/// Per-type parameters from CoreSpec — Observation fades fast, Heuristic nearly immortal.
/// </summary>
public static class FadeMemCurve
{
    public static readonly IReadOnlyDictionary<MemoryType, (double HalfLifeDays, double Shape)> Defaults =
        new Dictionary<MemoryType, (double, double)>
        {
            [MemoryType.Observation] = (30, 1.2),
            [MemoryType.Insight]     = (90, 1.0),
            [MemoryType.Procedure]   = (365, 0.8),
            [MemoryType.Heuristic]   = (730, 0.7),
        };

    public const float Floor = 0.05f;

    /// <summary>Returns the decayed importance, floored at <see cref="Floor"/>.</summary>
    public static float Decay(float importance, float confidence, double daysSinceCreation, MemoryType type)
    {
        var (halfLife, shape) = Defaults[type];
        var confidenceBoost = 1.0 + (confidence - 0.5) * 0.5;
        var adjustedHalfLife = halfLife * confidenceBoost;
        var normalizedAge = Math.Max(0, daysSinceCreation) / adjustedHalfLife;
        var shapedAge = Math.Pow(normalizedAge, shape);
        var decayFactor = Math.Pow(2, -shapedAge);
        return Math.Max(Floor, (float)(importance * decayFactor));
    }
}
