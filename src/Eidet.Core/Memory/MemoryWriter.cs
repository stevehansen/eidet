using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Memory;

/// <summary>
/// Owns every code path that mutates memory state: store, supersede, forget, feedback,
/// curation edits, and cross-repo links. Each mutation invalidates the recall cache and
/// fires the matching pre/post hook. Read paths live in <see cref="MemoryRecall"/> and
/// <see cref="MemoryQueries"/>.
/// </summary>
internal sealed class MemoryWriter
{
    private const float DuplicateThreshold = 0.92f;

    private readonly IEidetStore _store;
    private readonly IHookRunner _hooks;
    private readonly RecallCache _cache;
    private readonly RepoActivityTracker _activity;

    public MemoryWriter(IEidetStore store, IHookRunner hooks, RecallCache cache, RepoActivityTracker activity)
    {
        _store = store;
        _hooks = hooks;
        _cache = cache;
        _activity = activity;
    }

    public async Task<StoreResult> StoreAsync(
        string repoId,
        string content,
        MemoryType type,
        List<string>? tags,
        float importance,
        string source,
        string? sessionId,
        string? supersedes,
        MemoryProvenance? provenance,
        CancellationToken ct)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        _activity.Track(normalizedRepoId);

        var preHook = await _hooks.RunPreHooksAsync(HookEvent.PreStore, new HookContext
        {
            Event = "pre-store",
            Repo = normalizedRepoId,
            Data = new { content, type = type.ToString().ToLowerInvariant(), tags, importance, source },
        }, ct);
        if (!preHook.Allowed)
            return StoreResult.Rejected($"Hook rejected: {preHook.Reason}");

        var gate = WriteValidator.Validate(content, type);
        if (!gate.Passed)
            return StoreResult.Rejected(gate.Reason!);

        var resolvedProvenance = provenance ?? ProvenanceResolver.FromSource(source);

        var duplicate = await _store.FindDuplicateAsync(normalizedRepoId, content, DuplicateThreshold, ct);
        if (duplicate is not null)
            return StoreResult.Duplicate(duplicate.Id);

        if (!string.IsNullOrEmpty(supersedes))
        {
            var old = await _store.GetAsync(supersedes, ct);
            if (old is not null)
            {
                old.IsLatest = false;
                old.Validity.ValidUntil = DateTime.UtcNow;
                old.ForgetReason = "Superseded by new memory";
                await _store.UpdateAsync(old, ct);
            }
        }

        var now = DateTime.UtcNow;
        var entry = new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(normalizedRepoId, type, content, now),
            RepoId = normalizedRepoId,
            Type = type,
            Content = content,
            Tags = tags ?? [],
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

        var id = await _store.StoreAsync(entry, ct);
        _cache.Invalidate();

        _ = _hooks.RunPostHooksAsync(HookEvent.PostStore, new HookContext
        {
            Event = "post-store",
            Repo = normalizedRepoId,
            Data = new { id, type = type.ToString().ToLowerInvariant(), content, tags, importance },
        }, ct);

        return StoreResult.Stored(id);
    }

    public async Task<bool> ForgetAsync(string id, string? reason, string? sessionId, CancellationToken ct)
    {
        var preHook = await _hooks.RunPreHooksAsync(HookEvent.PreForget, new HookContext
        {
            Event = "pre-forget",
            Repo = "",
            Data = new { id, reason },
        }, ct);
        if (!preHook.Allowed)
            return false;

        var forgotten = await _store.ForgetAsync(id, ct);
        if (!forgotten) return false;

        if (!string.IsNullOrEmpty(reason))
        {
            var original = await _store.GetAsync(id, ct);
            if (original is not null)
            {
                original.ForgetReason = reason;
                await _store.UpdateAsync(original, ct);

                var now = DateTime.UtcNow;
                var observation = new MemoryEntry
                {
                    Id = MemoryIdGenerator.Generate(original.RepoId, MemoryType.Observation, reason, now),
                    RepoId = original.RepoId,
                    Type = MemoryType.Observation,
                    Content = $"Forgot memory [{id}]: {reason}",
                    Source = "system",
                    SourceSessionId = sessionId,
                    CreatedAt = now,
                    Validity = new Validity { ValidFrom = now },
                    Importance = 0.1f,
                    DerivedFrom = [id],
                };
                await _store.StoreAsync(observation, ct);
            }
        }

        _cache.Invalidate();

        _ = _hooks.RunPostHooksAsync(HookEvent.PostForget, new HookContext
        {
            Event = "post-forget",
            Repo = "",
            Data = new { id, reason },
        }, ct);

        return true;
    }

    public async Task<bool> ApplyFeedbackAsync(string memoryId, bool wasUsed, CancellationToken ct)
    {
        var entry = await _store.GetAsync(memoryId, ct);
        if (entry is null) return false;

        if (wasUsed)
        {
            entry.EchoCount++;
            entry.Importance = Math.Min(1.0f, entry.Importance + 0.05f);
            entry.Confidence = Math.Min(1.0f, entry.Confidence + 0.1f);
        }
        else
        {
            entry.FizzleCount++;
            entry.Importance = Math.Max(0.05f, entry.Importance - 0.1f);
            entry.Confidence = Math.Max(0.0f, entry.Confidence - 0.15f);
        }

        entry.LastAccessedAt = DateTime.UtcNow;
        entry.AccessCount++;
        await _store.UpdateAsync(entry, ct);
        _cache.Invalidate();
        return true;
    }

    public async Task<bool> UpdateAsync(
        string id,
        string? content,
        List<string>? tags,
        float? importance,
        float? confidence,
        MemoryType? type,
        string? oneLiner,
        string? summary,
        string? foresightHint,
        CancellationToken ct)
    {
        var entry = await _store.GetAsync(id, ct);
        if (entry is null) return false;

        var contentChanged = content != null && content != entry.Content;

        if (contentChanged)
        {
            var gate = WriteValidator.Validate(content!, type ?? entry.Type);
            if (!gate.Passed) return false;

            entry.IsLatest = false;
            entry.Validity.ValidUntil = DateTime.UtcNow;
            entry.ForgetReason = "Superseded by user edit";
            await _store.UpdateAsync(entry, ct);

            var now = DateTime.UtcNow;
            var newEntry = new MemoryEntry
            {
                Id = MemoryIdGenerator.Generate(entry.RepoId, type ?? entry.Type, content!, now),
                RepoId = entry.RepoId,
                Type = type ?? entry.Type,
                Content = content!,
                Tags = tags ?? entry.Tags,
                Importance = importance.HasValue ? Math.Clamp(importance.Value, 0f, 1f) : entry.Importance,
                Confidence = confidence.HasValue ? Math.Clamp(confidence.Value, 0f, 1f) : entry.Confidence,
                Source = entry.Source,
                SourceSessionId = entry.SourceSessionId,
                CreatedAt = now,
                Validity = new Validity { ValidFrom = now },
                ParentMemoryId = entry.Id,
                IsLatest = true,
                Provenance = MemoryProvenance.UserStated,
                Entities = EntityExtractor.Extract(content!),
                OneLiner = EntityExtractor.GenerateHeuristicOneLiner(content!),
                EchoCount = entry.EchoCount,
                FizzleCount = entry.FizzleCount,
                AccessCount = entry.AccessCount,
                Links = entry.Links,
                DerivedFrom = entry.DerivedFrom,
            };

            await _store.StoreAsync(newEntry, ct);
        }
        else
        {
            if (tags != null) entry.Tags = tags;
            if (importance.HasValue) entry.Importance = Math.Clamp(importance.Value, 0f, 1f);
            if (confidence.HasValue) entry.Confidence = Math.Clamp(confidence.Value, 0f, 1f);
            if (type.HasValue) entry.Type = type.Value;
            if (oneLiner != null) entry.OneLiner = oneLiner;
            if (summary != null) entry.Summary = summary;
            if (foresightHint != null) entry.ForesightHint = foresightHint;
            await _store.UpdateAsync(entry, ct);
        }

        _cache.Invalidate();
        return true;
    }

    public async Task<bool> AddLinkAsync(
        string memoryId, string targetRepoId, string relation, string? targetMemoryId, CancellationToken ct)
    {
        var entry = await _store.GetAsync(memoryId, ct);
        if (entry is null) return false;

        var normalized = RepoIdNormalizer.Normalize(targetRepoId);
        var exists = entry.Links.Any(l =>
            string.Equals(l.TargetRepoId, normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Relation, relation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.TargetMemoryId, targetMemoryId, StringComparison.OrdinalIgnoreCase));
        if (exists) return true;

        entry.Links.Add(new MemoryLink
        {
            TargetRepoId = normalized,
            TargetMemoryId = targetMemoryId,
            Relation = relation,
        });

        await _store.UpdateAsync(entry, ct);
        _cache.Invalidate();
        return true;
    }

    public async Task<bool> RemoveLinkAsync(string memoryId, string targetRepoId, string relation, CancellationToken ct)
    {
        var entry = await _store.GetAsync(memoryId, ct);
        if (entry is null) return false;

        var normalized = RepoIdNormalizer.Normalize(targetRepoId);
        var removed = entry.Links.RemoveAll(l =>
            string.Equals(l.TargetRepoId, normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Relation, relation, StringComparison.OrdinalIgnoreCase));

        if (removed == 0) return false;

        await _store.UpdateAsync(entry, ct);
        _cache.Invalidate();
        return true;
    }
}
