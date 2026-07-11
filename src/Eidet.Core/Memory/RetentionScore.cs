using Eidet.Core.Domain;
using Eidet.Core.Maintenance;

namespace Eidet.Core.Memory;

/// <summary>
/// Derived-never-stored eviction-ordering key (#39): decayed Importance lifted by echo usage. Used
/// ONLY by <c>BudgetEvictionStage</c> to pick eviction victims (lowest retention first) — it never
/// enters recall fusion, so there is no double-count with the UCB exploration bonus (UCB rewards
/// rarely-surfaced memories at recall-rank; reinforcement here rewards heavily-echoed memories at
/// eviction-time — opposite direction, disjoint consumer). Mirrors the derived-not-stored pattern of
/// <see cref="MemoryTrust"/> / <see cref="MemoryRoi"/>: recomputed per use from live fields, nothing
/// forgeable is persisted.
/// </summary>
public static class RetentionScore
{
    /// <summary>
    /// <c>Importance · recency · (1 + β·ln(1 + EchoCount))</c>. Importance is the already-decayed stored
    /// field (BudgetEviction runs after ImportanceDecay + RoiDecay); recency is the per-type dual-clock
    /// FadeMem factor (a memory recalled recently stays retained); reinforcement is monotone in <i>useful</i>
    /// uses, log-smoothed so a single echo isn't decisive. Higher = more worth keeping.
    /// </summary>
    public static double Of(MemoryEntry e, DateTime now, double beta)
    {
        var recency = FadeMemCurve.Recency(e.CreatedAt, e.LastAccessedAt, now, e.Type);
        var reinforcement = 1.0 + beta * Math.Log(1.0 + e.EchoCount);
        return e.Importance * recency * reinforcement;
    }
}
