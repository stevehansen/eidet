namespace Eidet.Core.Domain;

/// <summary>
/// Every memory is a single RavenDB document with a deterministic ID.
/// ID format: "memories/{repoId}/{type}/{shortHash}"
/// shortHash = first 12 chars of SHA256(content + createdAt)
/// </summary>
public class MemoryEntry
{
    /// <summary>
    /// Content prefix marking a redaction tombstone (written by <c>MemoryService.RedactAsync</c>).
    /// A tombstone is permanently un-enrichable: enrichment refuses it so the scrubbed payload is
    /// never re-described by an LLM.
    /// </summary>
    public const string RedactedPrefix = "[redacted:";

    public string Id { get; set; } = "";

    // Namespace isolation
    public string RepoId { get; set; } = "";
    public string? LayerId { get; set; } // null = local layer

    // Classification
    public MemoryType Type { get; set; }
    public Valence Valence { get; set; } = Valence.Neutral;
    public FunctionalStage Stage { get; set; } = FunctionalStage.None;
    public List<string> Tags { get; set; } = [];

    // Content
    public string Content { get; set; } = "";
    public string? Summary { get; set; }
    public string? OneLiner { get; set; }
    public List<string> Entities { get; set; } = [];

    // Temporal
    public DateTime CreatedAt { get; set; }
    public Validity Validity { get; set; } = new();
    public DateTime? ForgetAfter { get; set; }
    public string? ForgetReason { get; set; }

    // Provenance
    public MemoryProvenance Provenance { get; set; } = MemoryProvenance.AgentInferred;
    public string Source { get; set; } = "";
    public string? SourceSessionId { get; set; }
    public List<string> DerivedFrom { get; set; } = [];

    // Version chain
    public string? ParentMemoryId { get; set; }
    public bool IsLatest { get; set; } = true;

    // Cross-repo linking
    public List<MemoryLink> Links { get; set; } = [];

    // Ranking
    public float Importance { get; set; } = 0.5f;
    public float Confidence { get; set; } = 0.7f;
    public int AccessCount { get; set; }
    public DateTime? LastAccessedAt { get; set; }

    /// <summary>
    /// Lexical share — normLex/(normLex+normVec), 0.5 when both 0 — of this memory at its most recent
    /// recall. Drives per-repo alpha learning: a later echo/fizzle attributes a free relevance label
    /// for the lexical-vs-vector blend back to this share. Null = never recalled under the v2 pipeline.
    /// </summary>
    public double? LastLexShare { get; set; }

    // Echo/Fizzle feedback
    public int EchoCount { get; set; }
    public int FizzleCount { get; set; }

    /// Most-recent fizzle's reason; null = never fizzled with a reason. Drives the steeper
    /// content-invalidating penalty tier at fizzle time. A later echo intentionally leaves this
    /// untouched — it records the last <i>fizzle's</i> reason, not the last feedback event.
    public FizzleReason? LastFizzleReason { get; set; }

    // Enrichment
    public string? ForesightHint { get; set; }

    // Drift review (null = never reviewed)
    public DriftReview? Drift { get; set; }

    // Write-time conflict verdict (null = not quarantined). Set when a store contradicted a
    // high-trust incumbent; downranks in recall until an echo clears it or Released reverses it.
    public QuarantineInfo? Quarantine { get; set; }

    // Last time a dedup/consolidation merge was vetoed against this memory by the recall-consistency
    // guard (#39). Null = never. A rejected merge forgets nothing; this stamp makes the decision
    // queryable via the quality dashboard without a separate audit collection.
    public DateTime? LastMergeRejectedAt { get; set; }
}
