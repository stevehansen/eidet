namespace Eidet.Core.Domain;

/// <summary>
/// Derived verdict recorded on an entry when a write-time conflict check found it contradicts a
/// high-trust incumbent. Stored on <see cref="MemoryEntry.Quarantine"/> (null = never quarantined),
/// mirroring the <see cref="DriftReview"/> pattern: append-only, no parallel collection, no join.
/// A quarantined memory is DOWNRANKED in recall (heavy de-boost), never hidden — it must stay
/// recallable so it can earn the echoes that clear it. An echo clears it; <see cref="Released"/>
/// is the human/agent one-edit reversal of a false positive, kept as a flag so the audit record survives.
/// </summary>
public sealed class QuarantineInfo
{
    public string ContradictedId { get; set; } = "";
    public Valence Stance { get; set; }             // the incoming memory's stance
    public Valence ContradictedStance { get; set; } // the incumbent's stance
    public float Similarity { get; set; }
    public double ContradictedTrust { get; set; }
    public string Reason { get; set; } = "";
    public DateTime QuarantinedAt { get; set; }
    public bool Released { get; set; }              // human/agent reversal of a false positive — one edit
}
