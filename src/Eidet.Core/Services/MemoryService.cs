using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Memory;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

public class MemoryService
{
    private const float DuplicateThreshold = 0.92f;

    private readonly IEidetStore _store;
    private readonly LayerService? _layers;
    private readonly IHookRunner _hooks;
    private readonly RecallCache _recallCache = new();
    private readonly RepoActivityTracker _activity = new();

    public int StalenessWarningDays { get; set; } = 7;

    public MemoryService(IEidetStore store, LayerService? layers = null, IHookRunner? hooks = null)
    {
        _store = store;
        _layers = layers;
        _hooks = hooks ?? NullHookRunner.Instance;
    }

    // ─── Store ───────────────────────────────────────────────────────────

    public async Task<StoreResult> StoreAsync(
        string repoId,
        string content,
        MemoryType type,
        List<string>? tags = null,
        float importance = 0.5f,
        string source = "claude-session",
        string? sessionId = null,
        string? supersedes = null,
        MemoryProvenance? provenance = null,
        CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        _activity.Track(normalizedRepoId);

        // Hook: pre-store
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

        // Resolve provenance
        var resolvedProvenance = provenance ?? ProvenanceResolver.FromSource(source);

        // Duplicate detection
        var duplicate = await _store.FindDuplicateAsync(normalizedRepoId, content, DuplicateThreshold, ct);
        if (duplicate is not null)
            return StoreResult.Duplicate(duplicate.Id);

        // Handle supersession
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
        _recallCache.Invalidate();

        // Hook: post-store (fire-and-forget)
        _ = _hooks.RunPostHooksAsync(HookEvent.PostStore, new HookContext
        {
            Event = "post-store",
            Repo = normalizedRepoId,
            Data = new { id, type = type.ToString().ToLowerInvariant(), content, tags, importance },
        }, ct);

        return StoreResult.Stored(id);
    }

    // ─── Recall ──────────────────────────────────────────────────────────

    public async Task<List<MemorySearchResult>> RecallAsync(
        string repoId,
        MemoryQuery query,
        CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        _activity.Track(normalizedRepoId);

        // Hook: pre-recall
        var preHook = await _hooks.RunPreHooksAsync(HookEvent.PreRecall, new HookContext
        {
            Event = "pre-recall",
            Repo = normalizedRepoId,
            Data = new { query = query.Text, limit = query.Limit, type = query.Type?.ToString().ToLowerInvariant(), tags = query.Tags },
        }, ct);
        if (!preHook.Allowed)
            return []; // Pre-recall rejection returns empty results

        // Check cache
        var cacheKey = RecallCache.ComputeKey(normalizedRepoId, query);
        if (_recallCache.TryGet(cacheKey, out var cached))
            return cached;

        // Resolve repo scope (layer-aware)
        var repoIds = _layers != null && query.CrossRepo
            ? await _layers.ResolveScopeAsync(normalizedRepoId, query.CrossRepo, ct)
            : new List<string> { normalizedRepoId };

        // Parallel hybrid search
        var textTask = _store.FullTextSearchAsync(repoIds, query, ct);
        var vectorTask = _store.VectorSearchAsync(repoIds, query, ct);
        await Task.WhenAll(textTask, vectorTask);

        var textResults = textTask.Result;
        var vectorResults = vectorTask.Result;

        // Merge and deduplicate
        var merged = new List<MemorySearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in textResults)
        {
            if (seen.Add(entry.Id))
                merged.Add(RecallScoring.ToSearchResult(entry, score: 1.0f));
        }

        foreach (var entry in vectorResults)
        {
            if (seen.Add(entry.Id))
                merged.Add(RecallScoring.ToSearchResult(entry, score: 0.9f));
        }

        // Post-processing
        var now = DateTime.UtcNow;
        foreach (var result in merged)
        {
            // Cross-repo de-boost
            if (!string.Equals(result.RepoId, normalizedRepoId, StringComparison.OrdinalIgnoreCase))
                result.Score *= 0.8f;

            // Staleness warning
            result.AgeDays = (int)(now - result.CreatedAt).TotalDays;
            if (result.AgeDays >= StalenessWarningDays)
                result.StalenessWarning = $"[stale: {result.AgeDays}d ago — verify before acting]";
        }

        // Apply type diversity budgets
        var budgeted = RecallScoring.ApplyTypeBudgets(merged, query.Limit);

        // Bump access count on local memories (fire-and-forget, don't block recall)
        _ = BumpAccessCountsAsync(budgeted, normalizedRepoId, ct);

        // Hook: post-recall (fire-and-forget)
        _ = _hooks.RunPostHooksAsync(HookEvent.PostRecall, new HookContext
        {
            Event = "post-recall",
            Repo = normalizedRepoId,
            Data = new { query = query.Text, resultCount = budgeted.Count },
        }, ct);

        _recallCache.Set(cacheKey, budgeted);

        return budgeted;
    }

    private async Task BumpAccessCountsAsync(List<MemorySearchResult> results, string repoId, CancellationToken ct)
    {
        try
        {
            foreach (var result in results)
            {
                // Only bump local memories (not cross-repo/layer results)
                if (!string.Equals(result.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entry = await _store.GetAsync(result.Id, ct);
                if (entry is null) continue;

                entry.AccessCount++;
                entry.LastAccessedAt = DateTime.UtcNow;
                await _store.UpdateAsync(entry, ct);
            }
        }
        catch { /* Non-critical — don't fail recall for access tracking */ }
    }

    // ─── Context (L0 + L1) ───────────────────────────────────────────────

    public async Task<string> GetContextAsync(
        string repoId,
        int maxTokens = 600,
        CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        _activity.Track(normalizedRepoId);

        var sb = new StringBuilder();

        // L0: Identity
        var counts = await _store.GetCountsByTypeAsync(normalizedRepoId, ct);
        var totalMemories = counts.Values.Sum();

        sb.Append($"[Memory: {totalMemories} entries");
        foreach (var (type, count) in counts.OrderBy(kv => kv.Key))
        {
            if (count > 0)
                sb.Append($", {count} {type.ToString().ToLowerInvariant()}s");
        }
        sb.AppendLine("]");

        var remainingTokens = maxTokens - RecallScoring.EstimateTokens(sb.Length);
        if (remainingTokens <= 0)
            return sb.ToString();

        // L1: Top-K scored memories
        var candidates = await _store.GetTopScoredAsync(
            normalizedRepoId,
            [MemoryType.Insight, MemoryType.Procedure, MemoryType.Heuristic],
            60, // Over-fetch for scoring and budgeting
            ct);

        var now = DateTime.UtcNow;
        var scored = candidates
            .Select(e => new { Entry = e, Score = RecallScoring.ComputeL1Score(e, now) })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Apply L1 type budgets
        const int maxItems = 20;
        var insightBudget = (int)Math.Ceiling(maxItems * 0.50);
        var procedureBudget = (int)Math.Ceiling(maxItems * 0.30);
        var heuristicBudget = maxItems - insightBudget - procedureBudget;

        var insightCount = 0;
        var procedureCount = 0;
        var heuristicCount = 0;

        foreach (var item in scored)
        {
            var e = item.Entry;
            var budget = e.Type switch
            {
                MemoryType.Insight => insightCount < insightBudget,
                MemoryType.Procedure => procedureCount < procedureBudget,
                MemoryType.Heuristic => heuristicCount < heuristicBudget,
                _ => false,
            };
            if (!budget) continue;

            var typePrefix = e.Type switch
            {
                MemoryType.Insight => "[I]",
                MemoryType.Procedure => "[P]",
                MemoryType.Heuristic => "[H]",
                _ => "[O]",
            };

            var content = e.OneLiner ?? e.Summary ?? e.Content;
            var line = $"{typePrefix} {content}";

            var lineTokens = RecallScoring.EstimateTokens(line.Length);
            if (lineTokens > remainingTokens)
                break;

            sb.AppendLine(line);
            remainingTokens -= lineTokens;

            switch (e.Type)
            {
                case MemoryType.Insight: insightCount++; break;
                case MemoryType.Procedure: procedureCount++; break;
                case MemoryType.Heuristic: heuristicCount++; break;
            }
        }

        return sb.ToString();
    }

    // ─── Forget ──────────────────────────────────────────────────────────

    public async Task<bool> ForgetAsync(
        string id,
        string? reason = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        // Hook: pre-forget
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

                // Audit trail: system observation
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

        _recallCache.Invalidate();

        // Hook: post-forget (fire-and-forget)
        _ = _hooks.RunPostHooksAsync(HookEvent.PostForget, new HookContext
        {
            Event = "post-forget",
            Repo = "",
            Data = new { id, reason },
        }, ct);

        return true;
    }

    // ─── Feedback ────────────────────────────────────────────────────────

    public async Task<bool> ApplyFeedbackAsync(
        string memoryId, bool wasUsed, CancellationToken ct = default)
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
        _recallCache.Invalidate();
        return true;
    }

    // ─── History ─────────────────────────────────────────────────────────

    public async Task<List<MemoryEntry>> GetVersionChainAsync(
        string memoryId, CancellationToken ct = default)
    {
        var chain = new List<MemoryEntry>();
        var current = await _store.GetAsync(memoryId, ct);
        if (current is null) return chain;

        chain.Add(current);

        var visited = new HashSet<string> { memoryId };
        while (!string.IsNullOrEmpty(current.ParentMemoryId) && visited.Add(current.ParentMemoryId))
        {
            current = await _store.GetAsync(current.ParentMemoryId, ct);
            if (current is null) break;
            chain.Add(current);
        }

        return chain;
    }

    public async Task<DatabaseInfo?> GetStoreInfoAsync(CancellationToken ct = default) =>
        await _store.GetDatabaseInfoAsync(ct);

    public async Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
        await _store.GetCountsByTypeAsync(repoId, ct);

    // ─── Update (curation) ────────────────────────────────────────────────

    public async Task<bool> UpdateMemoryAsync(
        string id,
        string? content = null,
        List<string>? tags = null,
        float? importance = null,
        float? confidence = null,
        MemoryType? type = null,
        string? oneLiner = null,
        string? summary = null,
        string? foresightHint = null,
        CancellationToken ct = default)
    {
        var entry = await _store.GetAsync(id, ct);
        if (entry is null) return false;

        var contentChanged = content != null && content != entry.Content;

        if (contentChanged)
        {
            var gate = WriteValidator.Validate(content!, type ?? entry.Type);
            if (!gate.Passed) return false;

            // Create new version (supersession)
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
            // Metadata-only update: modify in place
            if (tags != null) entry.Tags = tags;
            if (importance.HasValue) entry.Importance = Math.Clamp(importance.Value, 0f, 1f);
            if (confidence.HasValue) entry.Confidence = Math.Clamp(confidence.Value, 0f, 1f);
            if (type.HasValue) entry.Type = type.Value;
            if (oneLiner != null) entry.OneLiner = oneLiner;
            if (summary != null) entry.Summary = summary;
            if (foresightHint != null) entry.ForesightHint = foresightHint;
            await _store.UpdateAsync(entry, ct);
        }

        _recallCache.Invalidate();
        return true;
    }

    // ─── Add Link ────────────────────────────────────────────────────────

    public async Task<bool> AddLinkAsync(
        string memoryId,
        string targetRepoId,
        string relation,
        string? targetMemoryId = null,
        CancellationToken ct = default)
    {
        var entry = await _store.GetAsync(memoryId, ct);
        if (entry is null) return false;

        // Avoid duplicate links
        var normalized = RepoIdNormalizer.Normalize(targetRepoId);
        var exists = entry.Links.Any(l =>
            string.Equals(l.TargetRepoId, normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Relation, relation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.TargetMemoryId, targetMemoryId, StringComparison.OrdinalIgnoreCase));
        if (exists) return true; // idempotent

        entry.Links.Add(new MemoryLink
        {
            TargetRepoId = normalized,
            TargetMemoryId = targetMemoryId,
            Relation = relation,
        });

        await _store.UpdateAsync(entry, ct);
        _recallCache.Invalidate();
        return true;
    }

    public async Task<bool> RemoveLinkAsync(
        string memoryId,
        string targetRepoId,
        string relation,
        CancellationToken ct = default)
    {
        var entry = await _store.GetAsync(memoryId, ct);
        if (entry is null) return false;

        var normalized = RepoIdNormalizer.Normalize(targetRepoId);
        var removed = entry.Links.RemoveAll(l =>
            string.Equals(l.TargetRepoId, normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Relation, relation, StringComparison.OrdinalIgnoreCase));

        if (removed == 0) return false;

        await _store.UpdateAsync(entry, ct);
        _recallCache.Invalidate();
        return true;
    }

    // ─── Browse ─────────────────────────────────────────────────────────

    public async Task<List<MemoryEntry>> BrowseAsync(
        string repoId, int skip = 0, int take = 50, MemoryType? type = null, CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        return await _store.BrowseAsync(normalizedRepoId, skip, take, type, ct);
    }

    public async Task<List<string>> GetRepoIdsAsync(CancellationToken ct = default) =>
        await _store.GetDistinctRepoIdsAsync(ct);

    public async Task<GraphData> GetGraphDataAsync(
        string repoId, int limit = 200, CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        var entries = await _store.BrowseAsync(normalizedRepoId, 0, limit, ct: ct);

        var nodes = entries.Select(e => new GraphNode
        {
            Id = e.Id,
            Type = e.Type,
            Label = e.OneLiner ?? e.Summary ?? StringUtils.Truncate(e.Content, 60),
            Importance = e.Importance,
            Confidence = e.Confidence,
            CreatedAt = e.CreatedAt,
            AccessCount = e.AccessCount,
            EchoCount = e.EchoCount,
            FizzleCount = e.FizzleCount,
            Tags = e.Tags,
            Entities = e.Entities,
        }).ToList();

        var idSet = new HashSet<string>(entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var edges = new List<GraphEdge>();

        foreach (var e in entries)
        {
            foreach (var parentId in e.DerivedFrom)
            {
                if (idSet.Contains(parentId))
                    edges.Add(new GraphEdge { From = parentId, To = e.Id, Relation = "derived" });
            }
            foreach (var link in e.Links)
            {
                if (!string.IsNullOrEmpty(link.TargetMemoryId) && idSet.Contains(link.TargetMemoryId))
                    edges.Add(new GraphEdge { From = e.Id, To = link.TargetMemoryId, Relation = link.Relation });
            }
        }

        return new GraphData { Nodes = nodes, Edges = edges };
    }

    public bool IsRepoActive(string repoId, int withinDays = 7) => _activity.IsActive(repoId, withinDays);
}
