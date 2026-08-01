using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;

namespace Eidet.Core.Gates;

public static class WriteValidator
{
    private static readonly string[] LowSignalPatterns =
    [
        "file exists",
        "file does not exist",
        "ran tests",
        "tests passed",
        "tests failed",
        "build succeeded",
        "build failed",
        "no changes",
        "no errors",
        "everything works",
        "it works",
        "fixed it",
        "done",
        "ok",
        "updated",
        "changed",
        "modified",
    ];

    public static ValidationResult Validate(string content, MemoryType type = MemoryType.Observation)
    {
        var secret = ScanSecrets(content);
        if (!secret.Passed) return secret;
        return CheckSignal(content, type);
    }

    /// <summary>
    /// The always-on 13-pattern secret scan, exposed on its own so surfaces that store
    /// non-semantic content (e.g. memory-tool files) can run it WITHOUT the low-signal/
    /// self-talk gates — those are semantic-store rules. Secret scanning itself is
    /// never optional on any write path.
    /// </summary>
    public static ValidationResult ScanSecrets(string content) => SecretScanRule.Check(content);

    /// <summary>
    /// Replace every secret-pattern match with a stable <c>[REDACTED:type]</c> marker.
    /// <paramref name="redactions"/> reports how many spans were rewritten.
    /// </summary>
    public static string RedactSecrets(string content, out int redactions) =>
        SecretScanRule.Redact(content, out redactions);

    /// <summary>
    /// Validate <paramref name="opts"/> content and build a fresh <see cref="MemoryEntry"/> ready for
    /// <c>StoreAsync</c>. Single canonical entry-construction path for new stored memories — keeps
    /// validation, id generation, default field population, and entity extraction in one place so
    /// the mutation path can't accidentally bypass any of them. Normalizes the repo id internally.
    /// </summary>
    public static EntryBuildResult BuildEntry(StoreOptions opts)
    {
        var repoId = RepoIdNormalizer.Normalize(opts.RepoId);

        var v = Validate(opts.Content, opts.Type);
        if (!v.Passed) return EntryBuildResult.Rejected(v.Reason ?? "rejected");

        var resolvedProvenance = opts.Provenance ?? ProvenanceResolver.FromSource(opts.Source);
        var now = DateTime.UtcNow;
        var entry = new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(repoId, opts.Type, opts.Content, now),
            RepoId = repoId,
            Type = opts.Type,
            Valence = opts.Valence,
            Stage = opts.Stage,
            Content = opts.Content,
            Tags = opts.Tags?.ToList() ?? [],
            Importance = Math.Clamp(opts.Importance, 0f, 1f),
            Source = opts.Source,
            SourceSessionId = opts.SessionId,
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            ParentMemoryId = opts.Supersedes,
            IsLatest = true,
            Provenance = resolvedProvenance,
            // Unknown sits with AgentInferred at the lower confidence: a write whose source this build
            // cannot map is LESS sure than a vouched-for one, so it must not gain confidence while losing
            // trust (#80).
            Confidence = resolvedProvenance is MemoryProvenance.AgentInferred or MemoryProvenance.Unknown
                ? 0.6f : 0.7f,
            DerivedFrom = opts.DerivedFrom?.ToList() ?? [],
            Entities = EntityExtractor.Extract(opts.Content),
            OneLiner = EntityExtractor.GenerateHeuristicOneLiner(opts.Content),
        };
        return EntryBuildResult.Built(entry);
    }

    /// <summary>
    /// Validate replacement content from <paramref name="edit"/> and build the supersession
    /// <see cref="MemoryEntry"/> that replaces <paramref name="original"/>. Carries forward stable
    /// counters (echo / fizzle / access) and the link / derived-from graph so curation edits don't
    /// drop history. A null <c>edit.Content</c> falls back to the original content, though callers
    /// normally route metadata-only edits to the in-place path instead.
    /// </summary>
    public static EntryBuildResult BuildEditEntry(MemoryEntry original, EditOptions edit)
    {
        var effectiveType = edit.Type ?? original.Type;
        var effectiveStage = edit.Stage ?? original.Stage;
        var newContent = edit.Content ?? original.Content;
        var validation = Validate(newContent, effectiveType);
        if (!validation.Passed) return EntryBuildResult.Rejected(validation.Reason ?? "rejected");

        var now = DateTime.UtcNow;
        var entry = new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(original.RepoId, effectiveType, newContent, now),
            RepoId = original.RepoId,
            Type = effectiveType,
            Valence = original.Valence,
            Stage = effectiveStage,
            Content = newContent,
            Tags = edit.Tags?.ToList() ?? original.Tags,
            Importance = edit.Importance.HasValue ? Math.Clamp(edit.Importance.Value, 0f, 1f) : original.Importance,
            Confidence = edit.Confidence.HasValue ? Math.Clamp(edit.Confidence.Value, 0f, 1f) : original.Confidence,
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

    private static ValidationResult CheckSignal(string content, MemoryType type)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ValidationResult.Fail("signal", "Content is empty.");

        var trimmed = content.Trim();

        if (trimmed.Length < 20)
            return ValidationResult.Fail("signal",
                $"Content too short ({trimmed.Length} chars). Memories should be specific and self-contained (20+ chars).");

        var lower = trimmed.ToLowerInvariant();
        foreach (var pattern in LowSignalPatterns)
        {
            if (lower == pattern || lower == pattern + ".")
                return ValidationResult.Fail("signal",
                    $"Content matches low-signal pattern (\"{pattern}\"). Store specific, actionable knowledge instead.");
        }

        if (type == MemoryType.Observation &&
            (lower.StartsWith("i will ") || lower.StartsWith("let me ") || lower.StartsWith("i'm going to ")))
            return ValidationResult.Fail("signal",
                "Content appears to be agent self-talk, not a project fact. Store what you learned, not what you're doing.");

        return ValidationResult.Pass();
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
