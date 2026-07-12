using Eidet.Core.Domain;

namespace Eidet.Service.Api;

public record StoreRequest
{
    public string Repo { get; init; } = "";
    public string Content { get; init; } = "";
    // Nullable so a REST caller can omit type and let the store handler apply the same
    // valence-driven default (negative/refuting ⇒ Heuristic) that the MCP surface does.
    public MemoryType? Type { get; init; }
    public List<string>? Tags { get; init; }
    public float? Importance { get; init; }
    public string? Source { get; init; }
    public string? SessionId { get; init; }
    public string? Supersedes { get; init; }
    public bool Negative { get; init; }
    public string? Valence { get; init; }
    public string? Stage { get; init; }
}

public record FeedbackRequest
{
    public string MemoryId { get; init; } = "";
    public bool WasUsed { get; init; }
    public string? Reason { get; init; }
}

public record PackExportRequest
{
    public string Repo { get; init; } = "";
    public string PackId { get; init; } = "";
    public string? BundleId { get; init; } // legacy alias for PackId
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string? OutputPath { get; init; }

    public string ResolvedPackId => !string.IsNullOrEmpty(PackId) ? PackId : (BundleId ?? "");
}

public record PackImportRequest
{
    public string Path { get; init; } = "";
}

public record CreateLinkRequest
{
    public string Repo { get; init; } = "";
    public string TargetRepo { get; init; } = "";
    public string Relation { get; init; } = "";
}

public record MountLayerRequest
{
    public string LayerId { get; init; } = "";
    public string Name { get; init; } = "";
    public LayerType Type { get; init; }
    public List<string>? ApplicableRepos { get; init; }
    public List<string>? ApplicablePackages { get; init; }
    public string? SourcePath { get; init; }
}

public record LayerSyncRequest
{
    public string Path { get; init; } = "";
    public string? LayerId { get; init; }
    public bool? Preview { get; init; }
    public bool? RemoveStale { get; init; }
}

public record UpdateMemoryRequest
{
    public string? Content { get; init; }
    public List<string>? Tags { get; init; }
    public float? Importance { get; init; }
    public float? Confidence { get; init; }
    public string? Type { get; init; }
    public string? Stage { get; init; }
    public string? OneLiner { get; init; }
    public string? Summary { get; init; }
    public string? ForesightHint { get; init; }
    /// <summary>Optimistic-concurrency precondition (#65); the <c>If-Match</c> header takes precedence.</summary>
    public string? ExpectedContentSha256 { get; init; }
}

public record RedactRequest
{
    public string Reason { get; init; } = "";
}

public record AddMemoryLinkRequest
{
    public string TargetRepoId { get; init; } = "";
    public string? TargetMemoryId { get; init; }
    public string Relation { get; init; } = "";
}

public record EnrichRequest
{
    public string Content { get; init; } = "";
    public string Task { get; init; } = "";  // oneliner, summary, foresight, entities
}
