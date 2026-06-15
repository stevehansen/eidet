namespace Eidet.Core.LooseEnds;

public enum LooseEndState { Open, Resolved }
public enum ResolutionKind { Done, Dropped, Promoted, Superseded }

/// <summary>
/// Open work an agent parked mid-task. A sibling of MemoryEntry, NOT a MemoryType.
/// ID: "looseends/{repoId}/{shortHash}". Lives in its own RavenDB collection so no
/// maintenance stage (FadeMem / consolidation / dedup / retention / TTL) ever touches it.
/// </summary>
public sealed class LooseEnd
{
    public string Id { get; set; } = "";
    public string RepoId { get; set; } = "";            // Local layer only; no LayerId

    public string Note { get; set; } = "";              // terse, speculative; secret-scanned, NOT signal-gated
    public List<string> Tags { get; set; } = [];        // ride-along match keys (v1: one list, no separate "Areas")
    public int Priority { get; set; } = 2;              // 1 high / 2 normal / 3 low — wake-up ordering only; never decays

    public LooseEndState State { get; set; } = LooseEndState.Open;
    public ResolutionKind? Resolution { get; set; }
    public string? ResolutionNote { get; set; }
    public string? PromotedToMemoryId { get; set; }     // set when Resolution == Promoted mints a MemoryEntry
    public string? ExternalRef { get; set; }            // e.g. "gh#412" when promoted to an issue instead of a memory

    public string Source { get; set; } = "claude-session";
    public string? SourceSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
