namespace Eidet.Core.Domain;

/// <summary>
/// Every memory is a single RavenDB document with a deterministic ID.
/// ID format: "memories/{repoId}/{type}/{shortHash}"
/// shortHash = first 12 chars of SHA256(content + createdAt)
/// </summary>
public class MemoryEntry
{
    public string Id { get; set; } = "";

    // Namespace isolation
    public string RepoId { get; set; } = "";
    public string? LayerId { get; set; } // null = local layer

    // Classification
    public MemoryType Type { get; set; }
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

    // Echo/Fizzle feedback
    public int EchoCount { get; set; }
    public int FizzleCount { get; set; }

    // Enrichment
    public string? ForesightHint { get; set; }
}
