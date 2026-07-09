using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// The single home for valence sign arithmetic — the three write choke points (store dup-gate,
/// dedup, consolidation) ask <see cref="Conflicts"/>/<see cref="Merge"/> a domain question and
/// never compute signs themselves.
/// </summary>
public static class ValencePolarity
{
    /// <summary>The polarity bucket key: +1 Affirming, -1 Refuting, 0 Neutral/Cautionary.</summary>
    public static int Sign(Valence v) => v switch
    {
        Valence.Affirming => 1,
        Valence.Refuting => -1,
        _ => 0,   // Neutral, Cautionary: no hard sign
    };

    /// <summary>True iff collapsing a and b would erase a contradiction (opposite hard signs).</summary>
    public static bool Conflicts(Valence a, Valence b) => Sign(a) * Sign(b) < 0;

    /// <summary>Survivor stance when a NON-conflicting pair merges: keep the opinionated one.</summary>
    public static Valence Merge(Valence a, Valence b) => a != Valence.Neutral ? a : b;
}
