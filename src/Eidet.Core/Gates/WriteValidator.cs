using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;

namespace Eidet.Core.Gates;

public static class WriteValidator
{
    private static readonly IValidationRule[] Rules =
    [
        new SecretScanRule(),
        new SignalRule(),
    ];

    public static ValidationResult Validate(string content, MemoryType type = MemoryType.Observation)
    {
        foreach (var rule in Rules)
        {
            var result = rule.Check(content, type);
            if (!result.Passed) return result;
        }
        return ValidationResult.Pass();
    }

    /// <summary>
    /// Validate <paramref name="content"/> and build a fresh <see cref="MemoryEntry"/> ready for
    /// <c>StoreAsync</c>. Single canonical entry-construction path for new stored memories — keeps
    /// validation, id generation, default field population, and entity extraction in one place so
    /// the mutation path can't accidentally bypass any of them.
    /// </summary>
    public static EntryBuildResult TryBuildStoreEntry(
        string normalizedRepoId,
        string content,
        MemoryType type,
        IReadOnlyList<string>? tags,
        float importance,
        string source,
        string? sessionId,
        string? supersedes,
        MemoryProvenance? provenance)
    {
        var validation = Validate(content, type);
        if (!validation.Passed) return EntryBuildResult.Rejected(validation.Reason ?? "rejected");

        var resolvedProvenance = provenance ?? ProvenanceResolver.FromSource(source);
        var now = DateTime.UtcNow;
        var entry = new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(normalizedRepoId, type, content, now),
            RepoId = normalizedRepoId,
            Type = type,
            Content = content,
            Tags = tags?.ToList() ?? [],
            Importance = Math.Clamp(importance, 0f, 1f),
            Source = source,
            SourceSessionId = sessionId,
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            ParentMemoryId = supersedes,
            IsLatest = true,
            Provenance = resolvedProvenance,
            Confidence = resolvedProvenance == MemoryProvenance.AgentInferred ? 0.6f : 0.7f,
            Entities = EntityExtractor.Extract(content),
            OneLiner = EntityExtractor.GenerateHeuristicOneLiner(content),
        };
        return EntryBuildResult.Built(entry);
    }

    /// <summary>
    /// Validate replacement <paramref name="newContent"/> and build the supersession <see cref="MemoryEntry"/>
    /// that replaces <paramref name="original"/>. Carries forward stable counters (echo / fizzle / access)
    /// and the link / derived-from graph so curation edits don't drop history.
    /// </summary>
    public static EntryBuildResult TryBuildEditEntry(
        MemoryEntry original,
        string newContent,
        MemoryType? type,
        IReadOnlyList<string>? tags,
        float? importance,
        float? confidence)
    {
        var effectiveType = type ?? original.Type;
        var validation = Validate(newContent, effectiveType);
        if (!validation.Passed) return EntryBuildResult.Rejected(validation.Reason ?? "rejected");

        var now = DateTime.UtcNow;
        var entry = new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(original.RepoId, effectiveType, newContent, now),
            RepoId = original.RepoId,
            Type = effectiveType,
            Content = newContent,
            Tags = tags?.ToList() ?? original.Tags,
            Importance = importance.HasValue ? Math.Clamp(importance.Value, 0f, 1f) : original.Importance,
            Confidence = confidence.HasValue ? Math.Clamp(confidence.Value, 0f, 1f) : original.Confidence,
            Source = original.Source,
            SourceSessionId = original.SourceSessionId,
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            ParentMemoryId = original.Id,
            IsLatest = true,
            Provenance = MemoryProvenance.UserStated,
            Entities = EntityExtractor.Extract(newContent),
            OneLiner = EntityExtractor.GenerateHeuristicOneLiner(newContent),
            EchoCount = original.EchoCount,
            FizzleCount = original.FizzleCount,
            AccessCount = original.AccessCount,
            Links = original.Links,
            DerivedFrom = original.DerivedFrom,
        };
        return EntryBuildResult.Built(entry);
    }
}

/// <summary>Outcome of a validated entry-construction attempt: either the built entry, or the rejection reason.</summary>
public readonly record struct EntryBuildResult
{
    public MemoryEntry? Entry { get; }
    public string? RejectionReason { get; }
    public bool IsBuilt => Entry is not null;

    private EntryBuildResult(MemoryEntry? entry, string? rejection)
    {
        Entry = entry;
        RejectionReason = rejection;
    }

    public static EntryBuildResult Built(MemoryEntry entry) => new(entry, null);
    public static EntryBuildResult Rejected(string reason) => new(null, reason);
}
