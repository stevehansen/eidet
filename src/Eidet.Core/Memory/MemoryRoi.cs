using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Derived, never-stored ROI factor — the realized-benefit demotion gate, ORTHOGONAL to
/// <see cref="MemoryTrust"/>. Trust is the anti-poisoning FLOOR (provenance/type) and never drops
/// below its floor; ROI expresses PERFORMANCE and can push a proven net-negative action memory
/// BELOW that trust floor. The two compose multiplicatively at recall.
///
/// Like trust, ROI is recomputed on every recall from the live echo/fizzle counts — there is no
/// stored ROI field to forge or let drift. It only penalizes the action-shaped types
/// (Procedure/Heuristic), and only once they have proven net-negative (fizzles &gt; echoes); the
/// positive side is already handled by UCB and trust's echo-lift. Non-action types always return 1.0.
/// </summary>
public static class MemoryRoi
{
    /// <summary>Smoothing constant — softens thin evidence so a single fizzle is not decisive
    /// (same rationale as <c>MemoryTrust.EchoSmoothing</c>). Reversible: one echo to parity restores 1.0.</summary>
    private const double EchoSmoothing = 3.0;

    /// <summary>
    /// ROI factor in (0, 1.0]. Returns 1.0 (no penalty) except for net-negative Procedure/Heuristic
    /// memories (<c>FizzleCount &gt; EchoCount</c>), where it is <c>(echo + K)/(fizzle + K)</c> with
    /// <c>K = 3.0</c>: 0e/1f → 0.75, 0e/3f → 0.5, 0e/5f → 0.375. The smoothing IS the conservatism —
    /// there is no separate min-evidence gate, because the recall de-boost is ephemeral and per-query.
    /// </summary>
    public static double Factor(MemoryEntry entry)
    {
        if (entry.Type is not (MemoryType.Procedure or MemoryType.Heuristic))
            return 1.0;
        if (entry.FizzleCount <= entry.EchoCount)
            return 1.0;
        return (entry.EchoCount + EchoSmoothing) / (entry.FizzleCount + EchoSmoothing);
    }
}
