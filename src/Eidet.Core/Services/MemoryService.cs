using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Layers;
using Eidet.Core.Memory;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

/// <summary>
/// Public surface for memory operations. Owns the cache invalidation invariant: every
/// store / forget / feedback / edit / link mutation funnels through <c>RunMutationAsync</c>,
/// which writes via a file-scoped <see cref="MutationCtx"/> ref-like gate and bumps the
/// recall cache's per-scope generation in a <c>finally</c> block. The storage write API
/// is unreachable from any code path outside this file.
/// </summary>
/// <remarks>
/// Recall coherence under concurrency is preserved by per-scope generation tokens in
/// <see cref="RecallCache"/> — recall takes a snapshot of the generations it queries and
/// drops its cache write if any moved during the query. The recall side-effect that
/// bumps <c>AccessCount</c> uses a separate <see cref="AccessTrackingCtx"/> backed by a
/// patch-only store API, so it cannot accidentally invalidate the cache or write any
/// other field.
/// </remarks>
public sealed class MemoryService
{
    private const float DuplicateThreshold = 0.92f;

    private readonly IEidetStore _store;
    private readonly IHookRunner _hooks;
    private readonly LayerService? _layers;
    private readonly RecallCache _cache = new();
    private readonly RepoActivityTracker _activity = new();

    public int StalenessWarningDays { get; set; } = 7;

    public MemoryService(IEidetStore store, LayerService? layers = null, IHookRunner? hooks = null)
    {
        _store = store;
        _layers = layers;
        _hooks = hooks ?? NullHookRunner.Instance;
    }

    // ─── Mutations ───────────────────────────────────────────────────

    /// <summary>
    /// Stores a memory through the validation gate, hook lifecycle, and recall-cache
    /// invalidation. 80% positional overload — covers the common case.
    /// </summary>
    public Task<StoreResult> StoreAsync(
        string repoId, string content, MemoryType type,
        IReadOnlyList<string>? tags = null, float importance = 0.5f,
        CancellationToken ct = default) =>
        StoreAsync(new StoreOptions(repoId, content, type)
        {
            Tags = tags,
            Importance = importance,
        }, ct);

    /// <summary>20% surface — supersession chains, custom provenance, session attribution, alternate sources.</summary>
    public async Task<StoreResult> StoreAsync(StoreOptions opts, CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(opts.RepoId);
        _activity.Track(normalizedRepoId);

        var built = WriteValidator.BuildEntry(opts);
        if (!built.IsBuilt)
            return StoreResult.Rejected(built.RejectionReason!);
        var entry = built.Entry!;

        // Duplicate detection runs before the gate — no point firing PreStore for content
        // we're going to deduplicate against an existing entry.
        var duplicate = await _store.FindDuplicateAsync(normalizedRepoId, opts.Content, DuplicateThreshold, ct);
        if (duplicate is not null)
            return StoreResult.Duplicate(duplicate.Id);

        var preCtx = new HookContext
        {
            Event = "pre-store",
            Repo = normalizedRepoId,
            Data = new { opts.Content, type = opts.Type.ToString().ToLowerInvariant(), opts.Tags, opts.Importance, opts.Source },
        };
        var postCtxFactory = (string id) => new HookContext
        {
            Event = "post-store",
            Repo = normalizedRepoId,
            Data = new { id, type = opts.Type.ToString().ToLowerInvariant(), opts.Content, opts.Tags, opts.Importance },
        };

        return await RunMutationAsync(
            scope: normalizedRepoId,
            pre: HookEvent.PreStore, preCtx: preCtx,
            post: HookEvent.PostStore, postCtxFactory: () => postCtxFactory(entry.Id),
            body: async ctx =>
            {
                if (!string.IsNullOrEmpty(opts.Supersedes))
                {
                    var old = await _store.GetAsync(opts.Supersedes, ct);
                    if (old is not null)
                    {
                        old.IsLatest = false;
                        old.Validity.ValidUntil = DateTime.UtcNow;
                        old.ForgetReason = "Superseded by new memory";
                        await ctx.WriteAsync(old, ct);
                    }
                }
                var id = await ctx.StoreNewAsync(entry, ct);
                return StoreResult.Stored(id);
            },
            denied: reason => StoreResult.Rejected($"Hook rejected: {reason}"),
            ct: ct);
    }

    public async Task<bool> ForgetAsync(string id, string? reason = null, string? sessionId = null, CancellationToken ct = default)
    {
        // Resolve scope for invalidation — we need the repo id of the entry being forgotten.
        var existing = await _store.GetAsync(id, ct);
        var scope = existing?.RepoId ?? "";

        var hookCtx = new HookContext { Event = "pre-forget", Repo = scope, Data = new { id, reason } };
        var postHookCtx = new HookContext { Event = "post-forget", Repo = scope, Data = new { id, reason } };

        var outcome = await RunMutationAsync(
            scope: scope,
            pre: HookEvent.PreForget, preCtx: hookCtx,
            post: HookEvent.PostForget, postCtxFactory: () => postHookCtx,
            body: async ctx =>
            {
                var forgotten = await ctx.ForgetAsync(id, ct);
                if (!forgotten) return false;

                if (!string.IsNullOrEmpty(reason))
                {
                    var original = await _store.GetAsync(id, ct);
                    if (original is not null)
                    {
                        original.ForgetReason = reason;
                        await ctx.WriteAsync(original, ct);

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
                        await ctx.StoreNewAsync(observation, ct);
                    }
                }

                return true;
            },
            denied: _ => false,
            ct: ct);
        return outcome;
    }

    public Task<bool> ApplyFeedbackAsync(string memoryId, bool wasUsed, CancellationToken ct = default) =>
        FeedbackAsync(memoryId, wasUsed, ct);

    public async Task<bool> FeedbackAsync(string memoryId, bool wasUsed, CancellationToken ct = default)
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

        return await RunMutationAsync(
            scope: entry.RepoId,
            pre: null, preCtx: null,
            post: null, postCtxFactory: null,
            body: async ctx =>
            {
                await ctx.WriteAsync(entry, ct);
                return true;
            },
            denied: _ => false,
            ct: ct);
    }

    public Task<bool> UpdateMemoryAsync(
        string id,
        string? content = null,
        IReadOnlyList<string>? tags = null,
        float? importance = null,
        float? confidence = null,
        MemoryType? type = null,
        string? oneLiner = null,
        string? summary = null,
        string? foresightHint = null,
        CancellationToken ct = default) =>
        EditAsync(id, new EditOptions
        {
            Content = content,
            Tags = tags,
            Importance = importance,
            Confidence = confidence,
            Type = type,
            OneLiner = oneLiner,
            Summary = summary,
            ForesightHint = foresightHint,
        }, ct);

    public async Task<bool> EditAsync(string id, EditOptions opts, CancellationToken ct = default)
    {
        var entry = await _store.GetAsync(id, ct);
        if (entry is null) return false;

        var contentChanged = opts.Content != null && opts.Content != entry.Content;

        return await RunMutationAsync(
            scope: entry.RepoId,
            pre: null, preCtx: null,
            post: null, postCtxFactory: null,
            body: async ctx =>
            {
                if (contentChanged)
                {
                    var built = WriteValidator.BuildEditEntry(entry, opts);
                    if (!built.IsBuilt) return false;

                    entry.IsLatest = false;
                    entry.Validity.ValidUntil = DateTime.UtcNow;
                    entry.ForgetReason = "Superseded by user edit";
                    await ctx.WriteAsync(entry, ct);
                    await ctx.StoreNewAsync(built.Entry!, ct);
                }
                else
                {
                    if (opts.Tags != null) entry.Tags = opts.Tags.ToList();
                    if (opts.Importance.HasValue) entry.Importance = Math.Clamp(opts.Importance.Value, 0f, 1f);
                    if (opts.Confidence.HasValue) entry.Confidence = Math.Clamp(opts.Confidence.Value, 0f, 1f);
                    if (opts.Type.HasValue) entry.Type = opts.Type.Value;
                    if (opts.OneLiner != null) entry.OneLiner = opts.OneLiner;
                    if (opts.Summary != null) entry.Summary = opts.Summary;
                    if (opts.ForesightHint != null) entry.ForesightHint = opts.ForesightHint;
                    await ctx.WriteAsync(entry, ct);
                }
                return true;
            },
            denied: _ => false,
            ct: ct);
    }

    public async Task<bool> AddLinkAsync(
        string memoryId, string targetRepoId, string relation, string? targetMemoryId = null, CancellationToken ct = default)
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

        return await RunMutationAsync(
            scope: entry.RepoId,
            pre: null, preCtx: null,
            post: null, postCtxFactory: null,
            body: async ctx => { await ctx.WriteAsync(entry, ct); return true; },
            denied: _ => false,
            ct: ct);
    }

    public async Task<bool> RemoveLinkAsync(
        string memoryId, string targetRepoId, string relation, CancellationToken ct = default)
    {
        var entry = await _store.GetAsync(memoryId, ct);
        if (entry is null) return false;

        var normalized = RepoIdNormalizer.Normalize(targetRepoId);
        var removed = entry.Links.RemoveAll(l =>
            string.Equals(l.TargetRepoId, normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Relation, relation, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        return await RunMutationAsync(
            scope: entry.RepoId,
            pre: null, preCtx: null,
            post: null, postCtxFactory: null,
            body: async ctx => { await ctx.WriteAsync(entry, ct); return true; },
            denied: _ => false,
            ct: ct);
    }

    // ─── Recall + Context ────────────────────────────────────────────

    /// <summary>80% positional recall — covers the common case.</summary>
    public Task<List<MemorySearchResult>> RecallAsync(
        string repoId, string queryText, int limit = 10, CancellationToken ct = default) =>
        RecallAsync(repoId, new RecallOptions(queryText) { Limit = limit }, ct);

    /// <summary>20% surface for full-filtered recall.</summary>
    public async Task<List<MemorySearchResult>> RecallAsync(
        string repoId, RecallOptions opts, CancellationToken ct = default)
    {
        var query = ToMemoryQuery(opts);
        var scope = await ResolveScopeAsync(repoId, opts.CrossRepo, ct);
        return await RecallInternalAsync(scope, query, ct);
    }

    private async Task<LayerScope> ResolveScopeAsync(string repoId, bool crossRepo, CancellationToken ct)
    {
        var normalized = RepoIdNormalizer.Normalize(repoId);
        return _layers != null && crossRepo
            ? await _layers.ResolveScopeAsync(normalized, crossRepo, ct)
            : LayerScope.Local(normalized);
    }

    private async Task<List<MemorySearchResult>> RecallInternalAsync(
        LayerScope scope, MemoryQuery query, CancellationToken ct)
    {
        _activity.Track(scope.PrimaryRepoId);

        var preHook = await _hooks.RunPreHooksAsync(HookEvent.PreRecall, new HookContext
        {
            Event = "pre-recall",
            Repo = scope.PrimaryRepoId,
            Data = new { query = query.Text, limit = query.Limit, type = query.Type?.ToString().ToLowerInvariant(), tags = query.Tags },
        }, ct);
        if (!preHook.Allowed) return [];

        var cacheKey = RecallCache.ComputeKey(scope.PrimaryRepoId, query);
        if (_cache.TryGet(cacheKey, scope.RepoIds, out var observed, out var cached))
            return cached;

        var repoIds = scope.RepoIds.ToList();
        var textTask = _store.FullTextSearchAsync(repoIds, query, ct);
        var vectorTask = _store.VectorSearchAsync(repoIds, query, ct);
        await Task.WhenAll(textTask, vectorTask);

        var merged = new List<MemorySearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in textTask.Result)
            if (seen.Add(entry.Id))
                merged.Add(RecallScoring.ToSearchResult(entry, score: 1.0f));
        foreach (var entry in vectorTask.Result)
            if (seen.Add(entry.Id))
                merged.Add(RecallScoring.ToSearchResult(entry, score: 0.9f));

        var now = DateTime.UtcNow;
        var staleAfter = StalenessWarningDays;
        foreach (var result in merged)
        {
            if (!scope.IsLocalRepo(result.RepoId))
                result.Score *= LayerScope.NonLocalDeBoost;
            result.AgeDays = (int)(now - result.CreatedAt).TotalDays;
            if (result.Drift is { Verdict: DriftVerdictKind.Stale or DriftVerdictKind.Contradicted } drift)
                result.StalenessWarning = $"[drift: {drift.Reason ?? drift.Verdict.ToString().ToLowerInvariant()}]";
            else if (result.AgeDays >= staleAfter)
                result.StalenessWarning = $"[stale: {result.AgeDays}d ago — verify before acting]";
        }

        var budgeted = RecallScoring.ApplyTypeBudgets(merged, query.Limit);

        _ = BumpAccessCountsAsync(budgeted, scope.PrimaryRepoId, ct);

        _ = _hooks.RunPostHooksAsync(HookEvent.PostRecall, new HookContext
        {
            Event = "post-recall",
            Repo = scope.PrimaryRepoId,
            Data = new { query = query.Text, resultCount = budgeted.Count },
        }, ct);

        // Set drops if any tracked scope's generation moved during the query.
        _cache.Set(cacheKey, observed, budgeted);

        return budgeted;
    }

    public async Task<string> GetContextAsync(string repoId, int maxTokens = 600, CancellationToken ct = default)
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
            60,
            ct);

        var now = DateTime.UtcNow;
        var scored = candidates
            .Select(e => new { Entry = e, Score = RecallScoring.ComputeL1Score(e, now) })
            .OrderByDescending(x => x.Score)
            .ToList();

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
            var withinBudget = e.Type switch
            {
                MemoryType.Insight => insightCount < insightBudget,
                MemoryType.Procedure => procedureCount < procedureBudget,
                MemoryType.Heuristic => heuristicCount < heuristicBudget,
                _ => false,
            };
            if (!withinBudget) continue;

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

    private async Task BumpAccessCountsAsync(List<MemorySearchResult> results, string repoId, CancellationToken ct)
    {
        // Access tracking is a justified exemption from cache invalidation: AccessCount /
        // LastAccessedAt are not in the cache key and do not affect recall scoring.
        // The dedicated patch-only ctx prevents this path from accidentally writing other fields.
        var ctx = new AccessTrackingCtx(_store);
        try
        {
            foreach (var result in results)
            {
                if (!string.Equals(result.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                    continue;
                await ctx.BumpAsync(result.Id, ct);
            }
        }
        catch { /* Non-critical — don't fail recall for access tracking */ }
    }

    // ─── Inspection (shallow — no cache, no invariant) ───────────────

    public async Task<List<MemoryEntry>> GetVersionChainAsync(string memoryId, CancellationToken ct = default)
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

    public Task<DatabaseInfo?> GetStoreInfoAsync(CancellationToken ct = default) =>
        _store.GetDatabaseInfoAsync(ct);

    public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
        _store.GetCountsByTypeAsync(repoId, ct);

    public Task<List<MemoryEntry>> BrowseAsync(
        string repoId, int skip = 0, int take = 50, MemoryType? type = null, CancellationToken ct = default) =>
        _store.BrowseAsync(RepoIdNormalizer.Normalize(repoId), skip, take, type, ct);

    public Task<List<string>> GetRepoIdsAsync(CancellationToken ct = default) =>
        _store.GetDistinctRepoIdsAsync(ct);

    public async Task<GraphData> GetGraphDataAsync(string repoId, int limit = 200, CancellationToken ct = default)
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

    public bool IsRepoActive(string repoId, int withinDays = 7) =>
        _activity.IsActive(repoId, withinDays);

    // ─── Bulk mutations ──────────────────────────────────────────────

    /// <summary>
    /// Bulk-stores a sequence of pre-built entries. Each entry's <see cref="MemoryEntry.RepoId"/>
    /// is recorded as a touched scope and invalidated exactly once when the loop completes (even
    /// on throw). With <see cref="BulkWriteOptions.SkipIfExists"/>, entries whose id already exists
    /// are counted as skipped rather than overwritten.
    /// </summary>
    public Task<BulkWriteResult> WriteManyAsync(
        IEnumerable<MemoryEntry> entries, BulkWriteOptions? options = null, CancellationToken ct = default)
    {
        options ??= new BulkWriteOptions();
        var bulkOpts = new BulkOptions
        {
            OperationName = "write-many",
            FireHooks = options.FireHooks,
            Validate = options.Validate,
        };
        return RunBulkAsync(async ctx =>
        {
            var added = 0;
            var skipped = 0;
            foreach (var entry in entries)
            {
                if (options.SkipIfExists && await ctx.GetAsync(entry.Id, ct) is not null)
                {
                    skipped++;
                    continue;
                }
                await ctx.StoreNewAsync(entry, ct);
                added++;
            }
            return new BulkWriteResult(added, skipped);
        }, bulkOpts, ct);
    }

    /// <summary>
    /// Bulk-updates a sequence of existing entries in place. Each entry's scope is recorded and
    /// invalidated once. Returns the number of entries written.
    /// </summary>
    public Task<int> UpdateManyAsync(IEnumerable<MemoryEntry> entries, CancellationToken ct = default) =>
        RunBulkAsync(async ctx =>
        {
            var count = 0;
            foreach (var entry in entries)
            {
                await ctx.WriteAsync(entry, ct);
                count++;
            }
            return count;
        }, new BulkOptions { OperationName = "update-many" }, ct);

    /// <summary>
    /// Escape hatch for mixed-op bulk bodies (store + update + delete). Hands the body a
    /// <see cref="BulkMutationCtx"/> whose write methods each record the touched scope; the
    /// surrounding pipeline invalidates each touched scope exactly once in a <c>finally</c>,
    /// including when the body throws — so callers never invalidate the recall cache by hand.
    /// </summary>
    /// <remarks>
    /// The touched-scope set is shared by reference and is not thread-safe; writes within a body
    /// must run sequentially (no <c>Task.WhenAll</c> over <see cref="BulkMutationCtx"/> writes).
    /// </remarks>
    public async Task<T> RunBulkAsync<T>(
        Func<BulkMutationCtx, Task<T>> body, BulkOptions? options = null, CancellationToken ct = default)
    {
        options ??= new BulkOptions();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ctx = new BulkMutationCtx(_store, touched, _hooks, options);
        try
        {
            return await body(ctx);
        }
        finally
        {
            _cache.InvalidateAll(touched);
        }
    }

    // ─── Internal gate ────────────────────────────────────────────────

    private static MemoryQuery ToMemoryQuery(RecallOptions opts) => new()
    {
        Text = opts.Query,
        Type = opts.Type,
        Tags = opts.Tags?.ToList() ?? [],
        Limit = opts.Limit,
        IncludeExpired = opts.IncludeExpired,
        CrossRepo = opts.CrossRepo,
    };

    private async Task<T> RunMutationAsync<T>(
        string scope,
        HookEvent? pre, HookContext? preCtx,
        HookEvent? post, Func<HookContext>? postCtxFactory,
        Func<MutationCtx, Task<T>> body,
        Func<string?, T> denied,
        CancellationToken ct)
    {
        if (pre is not null && preCtx is not null)
        {
            var preResult = await _hooks.RunPreHooksAsync(pre.Value, preCtx, ct);
            if (!preResult.Allowed) return denied(preResult.Reason);
        }

        var ctx = new MutationCtx(_store);
        try
        {
            return await body(ctx);
        }
        finally
        {
            // Generation bump: every recall that started before this point will drop its cache
            // write on Set; every recall that starts after sees the new generation.
            _cache.Invalidate(scope);
            if (post is not null && postCtxFactory is not null)
                _ = _hooks.RunPostHooksAsync(post.Value, postCtxFactory(), default);
        }
    }
}

/// <summary>
/// Storage write gate. The only code path with access to <see cref="IEidetStore"/>'s
/// write API from inside <see cref="MemoryService"/> — every mutation must accept a <c>MutationCtx</c>
/// from <c>RunMutationAsync</c> and call its methods, which guarantees the cache invalidation
/// in the surrounding <c>finally</c> block fires.
/// </summary>
/// <remarks>
/// Internal-but-not-file-local because file-scoped types can't appear in private generic
/// member signatures. The "only constructible from MemoryService.cs" property is enforced
/// by convention + the internal constructor — no other type in the assembly should call
/// <see cref="MutationCtx(IEidetStore)"/>.
/// </remarks>
internal readonly struct MutationCtx
{
    private readonly IEidetStore _store;

    internal MutationCtx(IEidetStore store) => _store = store;

    public Task<string> StoreNewAsync(MemoryEntry entry, CancellationToken ct) => _store.StoreAsync(entry, ct);
    public Task WriteAsync(MemoryEntry entry, CancellationToken ct) => _store.UpdateAsync(entry, ct);
    public Task<bool> ForgetAsync(string id, CancellationToken ct) => _store.ForgetAsync(id, ct);
}

/// <summary>
/// Bulk-mutation gate handed to a <see cref="MemoryService.RunBulkAsync{T}"/> body. Every write
/// records its scope into a set shared with the surrounding pipeline, which invalidates each
/// touched scope exactly once (including on throw). Public because it appears in the public
/// <c>RunBulkAsync</c> signature; the constructor is internal so only <see cref="MemoryService"/>
/// can hand one out. A <c>readonly struct</c> (not a <c>ref struct</c>): it lives inside the async
/// state machine produced by <c>Func&lt;BulkMutationCtx, Task&lt;T&gt;&gt;</c>.
/// </summary>
public readonly struct BulkMutationCtx
{
    private readonly IEidetStore _store;
    private readonly HashSet<string> _touched;
    private readonly IHookRunner _hooks;
    private readonly BulkOptions _options;

    internal BulkMutationCtx(IEidetStore store, HashSet<string> touched, IHookRunner hooks, BulkOptions options)
    {
        _store = store;
        _touched = touched;
        _hooks = hooks;
        _options = options;
    }

    public async Task<string> StoreNewAsync(MemoryEntry entry, CancellationToken ct)
    {
        if (_options.Validate)
        {
            var result = WriteValidator.Validate(entry.Content, entry.Type);
            if (!result.Passed)
                throw new InvalidOperationException($"Bulk write rejected: {result.Reason}");
        }

        var id = await _store.StoreAsync(entry, ct);
        _touched.Add(entry.RepoId);

        // Pre-store hook gating is intentionally unsupported in bulk; only the post-store
        // notification fires (opt-in, fire-and-forget).
        if (_options.FireHooks)
            _ = _hooks.RunPostHooksAsync(HookEvent.PostStore, new HookContext
            {
                Event = "post-store",
                Repo = entry.RepoId,
                Data = new { id, type = entry.Type.ToString().ToLowerInvariant() },
            }, default);

        return id;
    }

    public async Task WriteAsync(MemoryEntry entry, CancellationToken ct)
    {
        await _store.UpdateAsync(entry, ct);
        _touched.Add(entry.RepoId);
    }

    public async Task<bool> ForgetAsync(string id, CancellationToken ct)
    {
        var existing = await _store.GetAsync(id, ct);
        var forgotten = await _store.ForgetAsync(id, ct);
        if (forgotten && existing is not null)
            _touched.Add(existing.RepoId);
        return forgotten;
    }

    public async Task<bool> HardDeleteAsync(string id, string scope, CancellationToken ct)
    {
        var deleted = await _store.HardDeleteAsync(id, ct);
        if (deleted)
            _touched.Add(scope);
        return deleted;
    }

    public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct) => _store.GetAsync(id, ct);
}

/// <summary>
/// Access-tracking gate. Exposes only the patch-only <c>BumpAsync</c> method — the API
/// itself cannot be tricked into writing fields other than <c>AccessCount</c> and
/// <c>LastAccessedAt</c>. Used by the recall path's access-count side-effect, which is a
/// justified exemption from cache invalidation (the patched fields are not in the cache key
/// and do not affect recall scoring).
/// </summary>
internal readonly struct AccessTrackingCtx
{
    private readonly IEidetStore _store;

    internal AccessTrackingCtx(IEidetStore store) => _store = store;

    public Task BumpAsync(string entryId, CancellationToken ct) =>
        _store.PatchAccessAsync(entryId, DateTime.UtcNow, ct);
}
