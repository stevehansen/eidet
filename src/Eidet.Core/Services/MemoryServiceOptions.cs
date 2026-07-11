using Eidet.Core.Domain;

namespace Eidet.Core.Services;

/// <summary>20% surface for <see cref="MemoryService.StoreAsync(StoreOptions, CancellationToken)"/>.</summary>
public sealed record StoreOptions(string RepoId, string Content, MemoryType Type)
{
    public IReadOnlyList<string>? Tags { get; init; }
    public float Importance { get; init; } = 0.5f;
    public string Source { get; init; } = "claude-session";
    public string? SessionId { get; init; }
    public string? Supersedes { get; init; }
    public MemoryProvenance? Provenance { get; init; }
    public Valence Valence { get; init; } = Valence.Neutral;
    public FunctionalStage Stage { get; init; } = FunctionalStage.None;
}

/// <summary>20% surface for <see cref="MemoryService.RecallAsync(string, RecallOptions, CancellationToken)"/>.</summary>
public sealed record RecallOptions(string Query)
{
    public int Limit { get; init; } = 10;
    public MemoryType? Type { get; init; }
    public Valence? Valence { get; init; }
    public FunctionalStage? Stage { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool IncludeExpired { get; init; }
    public bool CrossRepo { get; init; } = true;

    /// <summary>Bounded graph-neighbor expansion of the candidate pool (default on; opt out for raw fusion).</summary>
    public bool ExpandGraph { get; init; } = true;

    /// <summary>Pins the lexical-vs-vector blend weight, bypassing the per-repo learned alpha. Null = use learned/default.</summary>
    public double? AlphaOverride { get; init; }
}

/// <summary>20% surface for <see cref="MemoryService.EditAsync(string, EditOptions, CancellationToken)"/>.</summary>
public sealed record EditOptions
{
    public string? Content { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public float? Importance { get; init; }
    public float? Confidence { get; init; }
    public MemoryType? Type { get; init; }
    public FunctionalStage? Stage { get; init; }
    public string? OneLiner { get; init; }
    public string? Summary { get; init; }
    public string? ForesightHint { get; init; }

    /// <summary>Reverse a quarantine false positive in one edit: marks the verdict <c>Released</c>
    /// (kept for the audit trail) so the recall de-boost no longer applies. No-op if not quarantined.</summary>
    public bool ReleaseQuarantine { get; init; }

    /// <summary>Optimistic-concurrency precondition (#65): the caller's expected SHA256 of the current
    /// content (see <see cref="Eidet.Core.Domain.ContentHash"/>). When set and it does not match the
    /// stored content, the edit is refused as <see cref="EditOutcome.PreconditionFailed"/> — no
    /// supersede. Null = today's blind last-write-wins behavior (backward compatible).</summary>
    public string? ExpectedContentSha256 { get; init; }
}

/// <summary>Outcome of <see cref="MemoryService.EditAsync"/> (#65): a precondition failure is a
/// first-class signal, distinct from success (metadata-<see cref="Updated"/> vs content-<see cref="Superseded"/>)
/// and from a missing/rejected target.</summary>
public enum EditOutcome { Updated, Superseded, NotFound, PreconditionFailed }

/// <summary>Options for <see cref="MemoryService.RunBulkAsync{T}"/>. Hooks and validation are off by default — bulk paths opt in.</summary>
public sealed record BulkOptions
{
    public string OperationName { get; init; } = "bulk";
    public bool FireHooks { get; init; }
    public bool Validate { get; init; }
}

/// <summary>Options for <see cref="MemoryService.WriteManyAsync"/>.</summary>
public sealed record BulkWriteOptions
{
    public bool SkipIfExists { get; init; }
    public bool FireHooks { get; init; }
    public bool Validate { get; init; }
}

/// <summary>Outcome of a <see cref="MemoryService.WriteManyAsync"/> call.</summary>
public sealed record BulkWriteResult(int Added, int Skipped);
