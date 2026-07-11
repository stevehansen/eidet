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

    // Alpha learning (#33 item 6): the EWMA smoothing factor and the clamp band that keeps the learned
    // lexical-vs-vector blend from ever collapsing to a single arm (the compensating control for shipping
    // alpha-learning alongside UCB). Applied at both learn-time and read-time.
    private const double EwmaLambda = 0.1;
    private const double AlphaMin = 0.15;
    private const double AlphaMax = 0.85;

    private readonly IEidetStore _store;
    private readonly IHookRunner _hooks;
    private readonly LayerService? _layers;
    private readonly RecallCache _cache = new();
    private readonly RepoActivityTracker _activity = new();

    public int StalenessWarningDays { get; set; } = 7;

    /// <summary>
    /// Optional Loose End surface for the wake-up slice in <see cref="GetContextAsync"/>. Settable
    /// (not a ctor dependency) because the promotion adapter wraps this service, so a ctor edge
    /// would be a construction cycle. When null the slice is empty (NullObject behavior).
    /// </summary>
    public LooseEnds.LooseEndService? LooseEnds { get; set; }

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
        // Polarity guard: a content-similar match that takes the OPPOSITE hard stance is a real
        // contradiction, not a duplicate — let it through so "X does not work" survives alongside "X works".
        if (duplicate is not null && !ValencePolarity.Conflicts(duplicate.Valence, entry.Valence))
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

    public Task<bool> ApplyFeedbackAsync(string memoryId, bool wasUsed, FizzleReason? reason = null, CancellationToken ct = default) =>
        FeedbackAsync(memoryId, wasUsed, reason, ct);

    // A fizzle optionally carries a reason: content-invalidating reasons (VersionDrift/Incorrect) cut
    // importance and confidence harder, since the memory's substance — not just its recall context — is wrong.
    public async Task<bool> FeedbackAsync(string memoryId, bool wasUsed, FizzleReason? reason = null, CancellationToken ct = default)
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
            entry.LastFizzleReason = reason;
            if (FizzleReasons.IsContentInvalidating(reason))
            {
                entry.Importance = Math.Max(0.05f, entry.Importance - 0.2f);
                entry.Confidence = Math.Max(0.0f, entry.Confidence - 0.3f);
            }
            else
            {
                entry.Importance = Math.Max(0.05f, entry.Importance - 0.1f);
                entry.Confidence = Math.Max(0.0f, entry.Confidence - 0.15f);
            }
        }
        entry.LastAccessedAt = DateTime.UtcNow;
        entry.AccessCount++;

        var written = await RunMutationAsync(
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

        // Per-repo alpha learning (#33 item 6): each echo/fizzle is a free relevance label for the
        // lexical-vs-vector blend. We nudge the learned alpha toward the lexical share that produced
        // this hit on an echo (the mix worked), and away from it on a fizzle (the mix misled). Runs
        // OUTSIDE the entry-write mutation — it patches RepoUsage, not a MemoryEntry, and must not
        // interfere with the entry-write cache invalidation. Best-effort; skips if this memory was
        // never surfaced under v2 (no LastLexShare attribution yet).
        if (written && entry.LastLexShare is { } lastLexShare)
        {
            try
            {
                // Echo nudges alpha toward the lexical share that surfaced this hit (the mix worked);
                // fizzle nudges away. The EWMA fold itself is applied server-side from the live stored
                // alpha (see UpdateRepoAlphaAsync) so concurrent feedback can't lose a step.
                var target = wasUsed ? lastLexShare : 1 - lastLexShare;
                await _store.UpdateRepoAlphaAsync(
                    entry.RepoId,
                    new AlphaEwmaUpdate(target, EwmaLambda, AlphaMin, AlphaMax, RecallWeights.Default.Alpha), ct);
            }
            catch { /* Non-critical — feedback already recorded; alpha tuning is an enhancement. */ }
        }

        return written;
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

        // The learned alpha is part of the cache key, so it must be resolved BEFORE TryGet — a learned
        // shift then invalidates cleanly via the bucket. One tiny Raven-cached doc load (the RepoUsage
        // anchor) on the recall path; cheap and bounded.
        var effectiveAlpha = await ResolveAlphaAsync(query, scope.PrimaryRepoId, ct);

        var cacheKey = RecallCache.ComputeKey(scope.PrimaryRepoId, query, Math.Round(effectiveAlpha, 1));
        if (_cache.TryGet(cacheKey, scope.RepoIds, out var observed, out var cached))
            return cached;

        var repoIds = scope.RepoIds.ToList();
        var lexTask = _store.SearchScoredAsync(SearchArm.Lexical, repoIds, query, ct);
        var vecTask = _store.SearchScoredAsync(SearchArm.Vector, repoIds, query, ct);
        await Task.WhenAll(lexTask, vecTask);

        var now = DateTime.UtcNow;
        var weights = RecallWeights.Default with
        {
            TotalN = ComputeTotalN(lexTask.Result, vecTask.Result),
            Alpha = effectiveAlpha,
        };
        var fused = RecallScoring.Fuse(lexTask.Result, vecTask.Result, weights, now);

        // Graph-neighbor expansion (#33 item 7) runs BEFORE trust gating / de-boost / budgeting so
        // link-reachable neighbors flow through exactly the same downstream policy as direct arm hits.
        fused = await ExpandNeighborsAsync(fused, scope, query, weights, now, ct);

        // Trust gating is a production-recall policy layered on top of Fuse, NOT folded into it:
        // the benchmark scorecard calls Fuse directly and would be unfairly penalized on its
        // Procedure/Heuristic gold cases. Here we hold poisoned/provisional memories down at
        // retrieval time alongside the non-local de-boost.
        var staleAfter = StalenessWarningDays;
        var merged = new List<MemorySearchResult>(fused.Count);
        // The lexical share of each candidate's surfacing mix — stamped onto the budgeted local results
        // so each future echo/fizzle becomes a free relevance label for alpha learning (#33 item 6).
        var lexShares = new Dictionary<string, double>(fused.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in fused)
        {
            var entry = candidate.Entry;
            var trust = MemoryTrust.Factor(entry);
            // ROI demotes proven net-negative Procedure/Heuristic memories below the trust floor —
            // it's a performance gate orthogonal to trust's anti-poisoning floor (1.0 otherwise).
            var roi = MemoryRoi.Factor(entry);
            var score = candidate.Fused * trust * roi;
            if (!scope.IsLocalRepo(entry.RepoId))
                score *= LayerScope.NonLocalDeBoost;

            var result = RecallScoring.ToSearchResult(entry, (float)score);
            result.TrustFactor = (float)trust;
            result.RoiFactor = (float)roi;
            result.AgeDays = (int)(now - result.CreatedAt).TotalDays;
            if (result.Drift is { Verdict: DriftVerdictKind.Stale or DriftVerdictKind.Contradicted } drift)
                result.StalenessWarning = $"[drift: {drift.Reason ?? drift.Verdict.ToString().ToLowerInvariant()}]";
            else if (result.AgeDays >= staleAfter)
                result.StalenessWarning = $"[stale: {result.AgeDays}d ago — verify before acting]";
            merged.Add(result);

            // Lexical share of the surfacing mix. 0.5 = deliberate no-arm-info prior — used when neither
            // arm scored the candidate (a graph neighbor enters with Lex=Vec=0), so a later echo on it
            // pulls alpha toward neutral rather than toward a phantom arm preference.
            var lv = candidate.Lex + candidate.Vec;
            lexShares[entry.Id] = lv > 0 ? candidate.Lex / lv : 0.5;
        }

        var budgeted = RecallScoring.ApplyTypeBudgets(merged, query.Limit);

        _ = BumpAccessCountsAsync(budgeted, scope.PrimaryRepoId, lexShares, ct);

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

    /// <summary>
    /// Effective lexical-vs-vector blend weight: an explicit <see cref="RecallOptions.AlphaOverride"/>
    /// wins, else the per-repo EWMA-learned <c>RepoUsage.AlphaLex</c>, else the cold-start default.
    /// Clamped to [<see cref="AlphaMin"/>, <see cref="AlphaMax"/>] so neither arm is ever fully muted.
    /// </summary>
    private async Task<double> ResolveAlphaAsync(MemoryQuery query, string primaryRepoId, CancellationToken ct)
    {
        var learned = query.AlphaOverride ?? await _store.GetRepoAlphaAsync(primaryRepoId, ct);
        return Math.Clamp(learned ?? RecallWeights.Default.Alpha, AlphaMin, AlphaMax);
    }

    /// <summary>
    /// Pulls link-reachable neighbors of the top fused candidates into the pool so they compete on the
    /// (damped) fused score. Best-effort: a failed neighbor load can't fail the whole recall (mirrors
    /// access tracking). Neighbor entries are loaded only if in scope and latest; the pure damped-
    /// inheritance math lives in <see cref="RecallScoring.ExpandNeighbors"/>.
    /// </summary>
    private async Task<List<FusedCandidate>> ExpandNeighborsAsync(
        List<FusedCandidate> fused, LayerScope scope, MemoryQuery query, RecallWeights weights, DateTime now, CancellationToken ct)
    {
        if (!query.ExpandGraph || fused.Count == 0) return fused;

        try
        {
            const int parentTopK = 10;
            const int maxNeighbors = 5;
            var present = new HashSet<string>(fused.Select(c => c.Entry.Id), StringComparer.OrdinalIgnoreCase);

            // In-scope = a repo this recall already searches (RepoIds always contains the primary, plus
            // any mounted layers). The de-boost loop downstream still de-boosts non-primary hits.
            bool InScope(string repoId) => scope.RepoIds.Contains(repoId, StringComparer.OrdinalIgnoreCase);

            // Gather neighbor ids from the strongest parents, deduped against the pool. Bounded by
            // maxNeighbors here too so we never over-fetch (the pure helper enforces the same cap).
            var toLoad = new List<string>();
            foreach (var parent in fused.Take(parentTopK))
            {
                if (toLoad.Count >= maxNeighbors) break;
                foreach (var link in parent.Entry.Links)
                {
                    if (toLoad.Count >= maxNeighbors) break;
                    var targetId = link.TargetMemoryId;
                    if (string.IsNullOrEmpty(targetId) || !present.Add(targetId)) continue;
                    // Cheap pre-filter on the link's declared target repo; the loaded entry's REAL repo
                    // is re-checked below (a stale/wrong TargetRepoId must not pull in an out-of-scope memory).
                    if (!InScope(link.TargetRepoId)) continue;
                    toLoad.Add(targetId);
                }
            }

            if (toLoad.Count == 0) return fused;

            var resolved = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in toLoad)
            {
                var entry = await _store.GetAsync(id, ct);
                // Authoritative scope check on the entry's actual RepoId — the link's declared
                // TargetRepoId is untrusted; never admit a memory from a repo this recall isn't searching.
                if (entry is { IsLatest: true } && InScope(entry.RepoId))
                    resolved[id] = entry;
            }

            return RecallScoring.ExpandNeighbors(
                fused, id => resolved.GetValueOrDefault(id), weights, now, parentTopK, maxNeighbors);
        }
        catch
        {
            // Expansion is an enhancement, not a correctness requirement — never fail recall over it.
            return fused;
        }
    }

    /// <summary>
    /// Diagnostic recall: runs the fuse pipeline (resolve scope → two scored arms →
    /// <see cref="RecallScoring.Fuse"/>) and returns the per-candidate component breakdown instead of
    /// budgeted results. Bypasses the recall cache and fires no hooks — it must not perturb live recall
    /// state. Uses the effective learned/overridden alpha so <see cref="RecallExplanation.AlphaUsed"/>
    /// reflects what production recall ranks with. NOTE: this is a PRE-EXPANSION view — graph-neighbor
    /// expansion (<see cref="ExpandNeighborsAsync"/>) is intentionally not run here, so link-reachable
    /// candidates that production recall would surface do not appear; the rows show the arm-fusion math only.
    /// </summary>
    public async Task<RecallExplanation> ExplainRecallAsync(
        string repoId, RecallOptions opts, CancellationToken ct = default)
    {
        var query = ToMemoryQuery(opts);
        var scope = await ResolveScopeAsync(repoId, opts.CrossRepo, ct);
        var repoIds = scope.RepoIds.ToList();
        var effectiveAlpha = await ResolveAlphaAsync(query, scope.PrimaryRepoId, ct);

        var lexTask = _store.SearchScoredAsync(SearchArm.Lexical, repoIds, query, ct);
        var vecTask = _store.SearchScoredAsync(SearchArm.Vector, repoIds, query, ct);
        await Task.WhenAll(lexTask, vecTask);

        var weights = RecallWeights.Default with
        {
            TotalN = ComputeTotalN(lexTask.Result, vecTask.Result),
            Alpha = effectiveAlpha,
        };
        var fused = RecallScoring.Fuse(lexTask.Result, vecTask.Result, weights, DateTime.UtcNow);

        var rows = fused
            .Select(c =>
            {
                var trust = MemoryTrust.Factor(c.Entry);
                return new RecallExplanationRow(
                    c.Entry.Id, c.Lex, c.Vec, c.Recency, c.Ucb, c.Fused, trust, c.Fused * trust);
            })
            .ToList();
        return new RecallExplanation(rows, weights.Alpha, rows.Count);
    }

    /// <summary>Σ (Echo+Fizzle) over the lex∪vec union (dedup by id) — the UCB exploration denominator base.</summary>
    private static long ComputeTotalN(IReadOnlyList<ScoredHit> lex, IReadOnlyList<ScoredHit> vec)
    {
        var seen = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in lex) seen.TryAdd(hit.Entry.Id, hit.Entry);
        foreach (var hit in vec) seen.TryAdd(hit.Entry.Id, hit.Entry);
        return seen.Values.Sum(e => (long)e.EchoCount + e.FizzleCount);
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

        // Loose End wake-up slice: open work surfaces here *because it is open*, not by relevance.
        // The L0 count addendum reflects the total open count; the slice itself is item- and
        // token-capped within a sub-budget carved from L1 (never from L0/identity).
        var openLooseEndCount = LooseEnds is not null
            ? await LooseEnds.CountOpenAsync(normalizedRepoId, ct)
            : 0;
        if (openLooseEndCount > 0)
            sb.Append($" | {openLooseEndCount} open loose ends");

        sb.AppendLine("]");

        var remainingTokens = maxTokens - RecallScoring.EstimateTokens(sb.Length);
        if (remainingTokens <= 0)
            return sb.ToString();

        if (LooseEnds is not null && openLooseEndCount > 0)
        {
            const int looseEndSubBudget = 120;
            var sliceBudget = Math.Max(0, Math.Min(looseEndSubBudget, remainingTokens));
            var slice = await LooseEnds.RenderWakeupSliceAsync(normalizedRepoId, sliceBudget, ct);
            if (slice.Length > 0)
            {
                sb.Append(slice);
                remainingTokens -= RecallScoring.EstimateTokens(slice.Length);
            }
        }

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
        const int procedureWakeupCap = 3; // hard cap (SWE-Skills-Bench: a wrongly-recalled procedure is net-negative; bound procedure pollution in the wake-up regardless of the soft type budget)
        var uncappedInsightBudget = (int)Math.Ceiling(maxItems * 0.50);
        var uncappedProcedureBudget = (int)Math.Ceiling(maxItems * 0.30);
        var heuristicBudget = maxItems - uncappedInsightBudget - uncappedProcedureBudget;
        var procedureBudget = Math.Min(uncappedProcedureBudget, procedureWakeupCap);
        // The slots the cap frees from procedures backfill insights — fully trusted (floor 1.0) — rather
        // than inflating heuristics, which are action-shaped and net-negative-if-wrong just like procedures.
        // Heuristics keep their own uncapped share; total wake-up item count is unchanged (maxItems).
        var insightBudget = maxItems - procedureBudget - heuristicBudget;

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

    private async Task BumpAccessCountsAsync(
        List<MemorySearchResult> results, string repoId, IReadOnlyDictionary<string, double> lexShares, CancellationToken ct)
    {
        // Access tracking is a justified exemption from cache invalidation: AccessCount /
        // LastAccessedAt are not in the recall cache key. LastAccessedAt does feed dual-clock
        // recency (#33), so a cached recall can be marginally stale on recency — bounded and
        // acceptable within the cache's short TTL; invalidating on every recall would defeat it.
        // We also stamp LastLexShare here (the surfacing mix that produced this hit) so a later
        // echo/fizzle on this memory can attribute a free relevance label to alpha learning (#33 item 6).
        // The dedicated patch-only ctx prevents this path from accidentally writing other fields.
        var ctx = new AccessTrackingCtx(_store);
        try
        {
            foreach (var result in results)
            {
                if (!string.Equals(result.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                    continue;
                await ctx.BumpAsync(result.Id, lexShares.TryGetValue(result.Id, out var s) ? s : null, ct);
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
        Valence = opts.Valence,
        Tags = opts.Tags?.ToList() ?? [],
        Limit = opts.Limit,
        IncludeExpired = opts.IncludeExpired,
        CrossRepo = opts.CrossRepo,
        ExpandGraph = opts.ExpandGraph,
        AlphaOverride = opts.AlphaOverride,
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
/// itself cannot be tricked into writing fields other than <c>AccessCount</c>,
/// <c>LastAccessedAt</c>, and <c>LastLexShare</c>. Used by the recall path's access-count
/// side-effect, which is a justified exemption from cache invalidation (the patched fields are
/// not in the recall cache key; the recency staleness LastAccessedAt can introduce is bounded by
/// the short cache TTL).
/// </summary>
internal readonly struct AccessTrackingCtx
{
    private readonly IEidetStore _store;

    internal AccessTrackingCtx(IEidetStore store) => _store = store;

    public Task BumpAsync(string entryId, double? lexShare, CancellationToken ct) =>
        _store.PatchAccessAsync(entryId, DateTime.UtcNow, lexShare, ct);
}
