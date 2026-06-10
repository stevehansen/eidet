namespace Eidet.Core.Domain;

public enum DriftVerdictKind { Ok, Stale, Contradicted, Vague }

/// <summary>
/// Verdict from the nightly LLM drift review. Stored on the entry itself;
/// null on <see cref="MemoryEntry.Drift"/> means never reviewed.
/// </summary>
public class DriftReview
{
    public DriftVerdictKind Verdict { get; set; }
    public float ModelConfidence { get; set; }   // model self-reported, clamped 0..1
    public string? Reason { get; set; }
    public string? SuggestedFix { get; set; }    // rewrite proposal; only a human applies it via the existing versioned edit
    public DateTime ReviewedAt { get; set; }     // doubles as the nightly coverage cursor
    public string Model { get; set; } = "";
}
