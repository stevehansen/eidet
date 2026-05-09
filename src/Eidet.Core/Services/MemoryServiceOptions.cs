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
}

/// <summary>20% surface for <see cref="MemoryService.RecallAsync(string, RecallOptions, CancellationToken)"/>.</summary>
public sealed record RecallOptions(string Query)
{
    public int Limit { get; init; } = 10;
    public MemoryType? Type { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool IncludeExpired { get; init; }
    public bool CrossRepo { get; init; } = true;
}

/// <summary>20% surface for <see cref="MemoryService.EditAsync(string, EditOptions, CancellationToken)"/>.</summary>
public sealed record EditOptions
{
    public string? Content { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public float? Importance { get; init; }
    public float? Confidence { get; init; }
    public MemoryType? Type { get; init; }
    public string? OneLiner { get; init; }
    public string? Summary { get; init; }
    public string? ForesightHint { get; init; }
}
