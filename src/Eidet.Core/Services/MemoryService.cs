using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Layers;
using Eidet.Core.Memory;
using Eidet.Core.Storage;
using Eidet.Core.Text;

namespace Eidet.Core.Services;

/// <summary>
/// Public surface for memory operations. Owns the cache invalidation invariant: every
/// store / forget / feedback / edit / link mutation funnels through <c>RunWriteAsync</c>
/// (store / forget via the hook-firing <c>RunMutationAsync</c> wrapper), which writes via
/// a file-scoped <see cref="MutationCtx"/> ref-like gate and bumps the recall cache's
/// per-scope generation in a <c>finally</c> block. The storage write API is unreachable
/// from any code path outside this file.
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

    /// <summary>
    /// Word-overlap above which two L1 lines are treated as the same fact and the lower-scored one
    /// loses its slot. Deliberately looser than <see cref="DuplicateThreshold"/>: that gate decides
    /// whether to REJECT a write, where a false positive silently loses knowledge, while this one
    /// only reorders a 20-line display and a false positive costs nothing but the next-best line.
    /// </summary>
    private const double L1DuplicateThreshold = 0.7;

    // Write-time conflict check (#37): a contradiction requires near-duplicate content AND opposite hard
    // valence signs AND a high-trust incumbent. The similarity floor for pulling conflict neighbors is
    // below the exact-dup threshold (a Refuting rephrase is not a byte-dup of the Affirming claim).
    private const float ConflictSimilarityFloor = 0.80f;
    private const int ConflictTopK = 5;
    // Heavy multiplicative recall de-boost for a quarantined memory — NOT 0.0: it must stay recallable so
    // it can earn the echoes that clear it (hiding it entirely causes cold-start starvation).
    private const double QuarantineDeBoost = 0.1;

    // Alpha learning (#33 item 6): the EWMA smoothing factor and the clamp band that keeps the learned
    // lexical-vs-vector blend from ever collapsing to a single arm (the compensating control for shipping
    // alpha-learning alongside UCB). Applied at both learn-time and read-time.
    private const double EwmaLambda = 0.1;
    private const double AlphaMin = 0.15;
    private const double AlphaMax = 0.85;

    // Distinct DerivedFrom targets /graph resolves outside its own window before calling a citation
    // dangling — without this, every citation across a >limit corpus would render as Missing.
    private const int CitationResolveCap = 200;

    private readonly IEidetStore _store;
    private readonly IHookRunner _hooks;
    private readonly IPoisonLog _poison;
    private readonly LayerService? _layers;
    private readonly TimeProvider _clock;
    private readonly RecallCache _cache = new();
    private readonly RepoActivityTracker _activity = new();

    public int StalenessWarningDays { get; set; } = 7;

    /// <summary>
    /// Optional Loose End surface for the wake-up slice in <see cref="GetContextAsync"/>. Settable
    /// (not a ctor dependency) because the promotion adapter wraps this service, so a ctor edge
    /// would be a construction cycle. When null the slice is empty (NullObject behavior).
    /// </summary>
    public LooseEnds.LooseEndService? LooseEnds { get; set; }

    /// <param name="clock">
    /// Trailing and optional so no existing call site changes. Used ONLY by <see cref="RedactAsync"/>: the
    /// amendment timestamp is part of the content shape the commitment check reads, so it has to be
    /// assertable. Every other clock read in this file is deliberately left on <c>DateTime.UtcNow</c>.
    /// </param>
    public MemoryService(
        IEidetStore store, LayerService? layers = null, IHookRunner? hooks = null, IPoisonLog? poison = null,
        TimeProvider? clock = null)
    {
        _store = store;
        _layers = layers;
        _hooks = hooks ?? NullHookRunner.Instance;
        _poison = poison ?? NullPoisonLog.Instance;
        _clock = clock ?? TimeProvider.System;
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

        // A supersession is an explicit, deliberate correction — its whole point is to contradict and
        // replace the incumbent — so it is EXEMPT from both the poison fast-path and the conflict gate.
        var isCorrection = !string.IsNullOrEmpty(opts.Supersedes);

        // Poison fast-path: a previously-quarantined content fingerprint short-circuits to Rejected
        // before any similarity query. Zero-overhead when the log is the NullPoisonLog default.
        if (!isCorrection)
        {
            var poison = await _poison.MatchAsync(normalizedRepoId, opts.Content, ct);
            if (poison is not null)
                return StoreResult.Rejected($"Matches recorded poison pattern (contradicts {poison.ContradictedId})");
        }

        // Duplicate detection runs before the gate — no point firing PreStore for content
        // we're going to deduplicate against an existing entry.
        var duplicate = await _store.FindDuplicateAsync(normalizedRepoId, opts.Content, DuplicateThreshold, ct);
        // A correction whose nearest match IS its own target is not a duplicate — replacing the
        // incumbent with near-identical content is exactly what a supersession does (e.g. a canon
        // page re-approved after a small edit). A match on any OTHER memory still dedupes below.
        if (duplicate is not null && isCorrection && duplicate.Id == opts.Supersedes)
            duplicate = null;
        // Polarity guard: a content-similar match that takes the OPPOSITE hard stance is a real
        // contradiction, not a duplicate — let it through so "X does not work" survives alongside "X works".
        if (duplicate is not null && !ValencePolarity.Conflicts(duplicate.Valence, entry.Valence))
            return StoreResult.Duplicate(duplicate.Id);

        // Write-time conflict check: only a hard-stance (Affirming/Refuting) incoming can contradict, so
        // the near-duplicate query — the one added cost — is skipped for the Neutral common case. A found
        // conflict quarantines the (still-stored) entry and records the attempt for the poison fast-path.
        ConflictFinding? conflict = null;
        if (!isCorrection && ValencePolarity.Sign(entry.Valence) != 0)
        {
            // Neighbor pool = same-type near-duplicates, PLUS the (type-agnostic) exact-duplicate already
            // fetched above when it takes the opposite stance — so a cross-type contradiction (e.g. a
            // Refuting Heuristic vs an Affirming Insight) is caught too, at no extra query.
            var neighbors = new List<MemoryEntry>(
                await _store.FindNearDuplicatesAsync(normalizedRepoId, entry, ConflictSimilarityFloor, ConflictTopK, ct));
            if (duplicate is not null && ValencePolarity.Conflicts(duplicate.Valence, entry.Valence)
                && neighbors.All(n => n.Id != duplicate.Id))
                neighbors.Add(duplicate);
            conflict = ConflictGate.Check(entry, neighbors);
            if (conflict is { } c)
            {
                entry.Quarantine = new QuarantineInfo
                {
                    ContradictedId = c.ContradictedId,
                    Stance = c.Stance,
                    ContradictedStance = c.ContradictedStance,
                    Similarity = c.Similarity,
                    ContradictedTrust = c.ContradictedTrust,
                    Reason = "Contradicts a high-trust memory",
                    QuarantinedAt = DateTime.UtcNow,
                };
                await _poison.RecordAsync(normalizedRepoId, c, opts.Content, ct);
            }
        }

        return await RunMutationAsync(
            MutationKind.Store, scope: normalizedRepoId,
            ctx: () => new HookContext
            {
                Repo = normalizedRepoId,
                Data = new { id = entry.Id, opts.Content, type = opts.Type.ToString().ToLowerInvariant(), opts.Tags, opts.Importance, opts.Source },
            },
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
                return conflict is { } cf ? StoreResult.QuarantinedPending(id, cf) : StoreResult.Stored(id);
            },
            denied: reason => StoreResult.Rejected($"Hook rejected: {reason}"),
            ct: ct);
    }

    public async Task<bool> ForgetAsync(string id, string? reason = null, string? sessionId = null, CancellationToken ct = default)
    {
        // Resolve scope for invalidation — we need the repo id of the entry being forgotten.
        var existing = await _store.GetAsync(id, ct);
        var scope = existing?.RepoId ?? "";

        var outcome = await RunMutationAsync(
            MutationKind.Forget, scope: scope,
            ctx: () => new HookContext { Repo = scope, Data = new { id, reason } },
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
                        // Id minted over the content actually stored (not over `reason`) so the audit
                        // record satisfies its own content commitment; provenance stamped explicitly so a
                        // first-party system write is never mistaken for unestablished provenance.
                        var auditContent = $"Forgot memory [{id}]: {reason}";
                        var observation = new MemoryEntry
                        {
                            Id = MemoryIdGenerator.Generate(original.RepoId, MemoryType.Observation, auditContent, now),
                            RepoId = original.RepoId,
                            Type = MemoryType.Observation,
                            Content = auditContent,
                            Source = "system",
                            Provenance = MemoryProvenance.System,
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
            // An echo is the signal a quarantined memory earned its place — clear the verdict so the
            // recall de-boost lifts. (A fizzle is left to the normal ForgetAfter/ROI machinery.)
            entry.Quarantine = null;
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

        var written = await RunWriteAsync(
            scope: entry.RepoId,
            body: async ctx =>
            {
                await ctx.WriteAsync(entry, ct);
                return true;
            },
            ct);

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

    public async Task<bool> UpdateMemoryAsync(
        string id,
        string? content = null,
        IReadOnlyList<string>? tags = null,
        float? importance = null,
        float? confidence = null,
        MemoryType? type = null,
        string? oneLiner = null,
        string? summary = null,
        string? foresightHint = null,
        CancellationToken ct = default)
    {
        // Legacy bool wrapper — no precondition, so PreconditionFailed can't occur; maps the two success
        // outcomes to true and NotFound/rejection to false, preserving its pre-#65 contract.
        var outcome = await EditAsync(id, new EditOptions
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
        return outcome is EditOutcome.Updated or EditOutcome.Superseded;
    }

    public async Task<EditOutcome> EditAsync(string id, EditOptions opts, CancellationToken ct = default)
    {
        var entry = await _store.GetAsync(id, ct);
        if (entry is null) return EditOutcome.NotFound;

        // Optimistic concurrency (#65): a caller that pinned the content it read is refused if the stored
        // content moved under it — no supersede, no lost update. Absent precondition = blind last-write-wins.
        if (opts.ExpectedContentSha256 is { Length: > 0 } expected
            && !string.Equals(expected, ContentHash.Of(entry.Content), StringComparison.OrdinalIgnoreCase))
            return EditOutcome.PreconditionFailed;

        var contentChanged = opts.Content != null && opts.Content != entry.Content;

        return await RunWriteAsync(
            scope: entry.RepoId,
            body: async ctx =>
            {
                if (contentChanged)
                {
                    var built = WriteValidator.BuildEditEntry(entry, opts);
                    // Content-gate rejection (secret/low-signal in the new content). Reported as NotFound
                    // to preserve the pre-#65 conflated "not found or update rejected" behavior.
                    if (!built.IsBuilt) return EditOutcome.NotFound;

                    entry.IsLatest = false;
                    entry.Validity.ValidUntil = DateTime.UtcNow;
                    entry.ForgetReason = "Superseded by user edit";
                    await ctx.WriteAsync(entry, ct);
                    await ctx.StoreNewAsync(built.Entry!, ct);
                    return EditOutcome.Superseded;
                }

                if (opts.Tags != null) entry.Tags = opts.Tags.ToList();
                if (opts.Importance.HasValue) entry.Importance = Math.Clamp(opts.Importance.Value, 0f, 1f);
                if (opts.Confidence.HasValue) entry.Confidence = Math.Clamp(opts.Confidence.Value, 0f, 1f);
                if (opts.Type.HasValue) entry.Type = opts.Type.Value;
                if (opts.Stage.HasValue) entry.Stage = opts.Stage.Value;
                if (opts.OneLiner != null) entry.OneLiner = opts.OneLiner;
                if (opts.Summary != null) entry.Summary = opts.Summary;
                if (opts.ForesightHint != null) entry.ForesightHint = opts.ForesightHint;
                // One-edit reversal of a quarantine false positive: mark Released (kept for the audit
                // trail) so the recall de-boost no longer applies.
                if (opts.ReleaseQuarantine && entry.Quarantine is { } q) q.Released = true;
                await ctx.WriteAsync(entry, ct);
                return EditOutcome.Updated;
            },
            ct);
    }

    /// <summary>
    /// Hard content erasure (#65) — the curation verb for GDPR erasure / secret cleanup, distinct from
    /// Forget (which soft-deletes but keeps content). Scrubs the sensitive payload of ANY node in a chain
    /// (latest or superseded) to a tombstone while preserving the audit structure: id, validity interval,
    /// IsLatest, lineage (ParentMemoryId/DerivedFrom), echo/fizzle/access counters, RepoId, Type all stay,
    /// so the chain remains walkable. Idempotent (re-redacting is a no-op). Writes a system audit
    /// observation (who/when/why), mirroring Forget. Off-MCP: REST/CLI/Web UI only.
    /// </summary>
    public async Task<bool> RedactAsync(string id, string reason, CancellationToken ct = default)
    {
        var entry = await _store.GetAsync(id, ct);
        if (entry is null) return false;
        if (entry.Content.StartsWith(MemoryEntry.RedactedPrefix, StringComparison.Ordinal)) return true; // idempotent

        var now = _clock.GetUtcNow().UtcDateTime;
        return await RunWriteAsync(
            scope: entry.RepoId,
            body: async ctx =>
            {
                // Scrub the sensitive payload. SearchText/SearchVector are index projections of these
                // fields, so re-indexing after this write drops the scrubbed content from search too.
                // Rendered through MemoryCommitment so this in-place rewrite classifies as Amended rather
                // than as tampering — the id is deliberately kept so the chain stays walkable.
                entry.Content = MemoryCommitment.Render("redacted", reason, now);
                // Empty string, not null: Summary == null means "awaiting enrichment" to the
                // EnrichmentWorker subscription, the nightly sweep, and the unenriched stats — a
                // tombstone must not re-enter any of those queues.
                entry.Summary = "";
                entry.OneLiner = null;
                entry.ForesightHint = null;
                entry.Entities = [];
                await ctx.WriteAsync(entry, ct);

                // Same as the forget audit record: id over the stored content, provenance stamped.
                var auditContent = $"Redacted memory [{id}]: {reason}";
                var observation = new MemoryEntry
                {
                    Id = MemoryIdGenerator.Generate(entry.RepoId, MemoryType.Observation, auditContent, now),
                    RepoId = entry.RepoId,
                    Type = MemoryType.Observation,
                    Content = auditContent,
                    Source = "system",
                    Provenance = MemoryProvenance.System,
                    CreatedAt = now,
                    Validity = new Validity { ValidFrom = now },
                    Importance = 0.1f,
                    DerivedFrom = [id],
                };
                await ctx.StoreNewAsync(observation, ct);
                return true;
            },
            ct);
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

        return await RunWriteAsync(
            scope: entry.RepoId,
            body: async ctx => { await ctx.WriteAsync(entry, ct); return true; },
            ct);
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

        return await RunWriteAsync(
            scope: entry.RepoId,
            body: async ctx => { await ctx.WriteAsync(entry, ct); return true; },
            ct);
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
        var absTask = _store.SearchScoredAsync(SearchArm.Abstraction, repoIds, query, ct);
        await Task.WhenAll(lexTask, vecTask, absTask);

        var now = DateTime.UtcNow;
        var weights = RecallWeights.Default with
        {
            TotalN = ComputeTotalN(lexTask.Result, vecTask.Result, absTask.Result),
            Alpha = effectiveAlpha,
        };
        var fused = RecallScoring.Fuse(lexTask.Result, vecTask.Result, absTask.Result, weights, now);

        // Both expansions (#33 item 7) run BEFORE trust gating / de-boost / budgeting so reachable
        // memories flow through exactly the same downstream policy as direct arm hits. Links first,
        // then cues: an authored link is the stronger signal, and running it first lets it claim a
        // memory at the higher decay before cue overlap can admit the same one at the lower.
        fused = await ExpandNeighborsAsync(fused, scope, query, weights, now, ct);
        fused = await ExpandEntitiesAsync(fused, scope, query, weights, now, ct);

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
            // Quarantine is a heavy DOWNRANK, never a hide: a memory that contradicts a high-trust
            // incumbent stays recallable (so it can earn the echo that clears it) but sinks below
            // trusted memories. An echo or a Released reversal removes the verdict entirely.
            if (entry.Quarantine is { Released: false })
                score *= QuarantineDeBoost;

            var result = RecallScoring.ToSearchResult(entry, (float)score);
            result.TrustFactor = (float)trust;
            result.RoiFactor = (float)roi;
            result.AgeDays = (int)(now - result.CreatedAt).TotalDays;
            if (result.Drift is { Verdict: DriftVerdictKind.Stale or DriftVerdictKind.Contradicted } drift)
                result.StalenessWarning = $"[drift: {drift.Reason ?? drift.Verdict.ToString().ToLowerInvariant()}]";
            else if (result.AgeDays >= staleAfter)
                result.StalenessWarning = $"[stale: {result.AgeDays}d ago — verify before acting]";
            merged.Add(result);

            // Lexical share of the surfacing mix. 0.5 = deliberate no-arm-info prior — used when no
            // arm scored the candidate (an expanded neighbor enters with Lex=Vec=Abs=0), so a later
            // echo on it pulls alpha toward neutral rather than toward a phantom arm preference.
            // The abstraction arm counts in the DENOMINATOR only: it is semantic evidence, so a hit
            // carried by it is evidence AGAINST this repo being lexical, never for it.
            var arms = candidate.Lex + candidate.Vec + candidate.Abs;
            lexShares[entry.Id] = arms > 0 ? candidate.Lex / arms : 0.5;
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
                // The ValidUntil guard is load-bearing for post-forget integrity: forget stamps ValidUntil
                // but leaves IsLatest=true, so an IsLatest-only check would resurface a forgotten memory
                // linked from a live parent (the leak the GraphNeighbor auditor probe targets).
                if (entry is { IsLatest: true } && entry.Validity.ValidUntil is null && InScope(entry.RepoId))
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
    /// Pulls cue-anchor matches — memories sharing an entity with the top fused candidates — into the
    /// pool so they compete on the (heavily damped) fused score. The complement to
    /// <see cref="ExpandNeighborsAsync"/>: that one follows links somebody authored, this one follows
    /// the entities enrichment extracted, so a related memory nobody ever linked is still reachable.
    /// Best-effort, exactly like link expansion — a failed cue lookup can't fail the whole recall.
    /// </summary>
    private async Task<List<FusedCandidate>> ExpandEntitiesAsync(
        List<FusedCandidate> fused, LayerScope scope, MemoryQuery query, RecallWeights weights, DateTime now, CancellationToken ct)
    {
        if (!query.ExpandEntities || fused.Count == 0) return fused;

        try
        {
            const int parentTopK = 10;
            const int maxCueMatches = 5;

            // Cues come from the strongest parents only — the same bound as link expansion, and for the
            // same reason: spreading activation should flow from the most-relevant hits, not from noise.
            var parents = fused.Take(parentTopK).ToList();
            var cues = parents
                .SelectMany(p => p.Entry.Entities)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cues.Count == 0) return fused;

            var repoIds = scope.RepoIds.ToList();
            var poolIds = fused.Select(c => c.Entry.Id).ToList();
            // Over-fetch: the store filters latest/valid, but the scope and in-pool checks below still
            // discard rows, and the pure helper caps admissions at maxCueMatches.
            var matches = await _store.FindByEntitiesAsync(repoIds, cues, poolIds, maxCueMatches * 4, ct);

            // Authoritative guards, identical to link expansion and load-bearing for the same reason:
            // forget stamps ValidUntil but leaves IsLatest=true, so an IsLatest-only check would
            // resurface a forgotten memory through this path. Scope is re-checked on the entry's REAL
            // RepoId — never admit a memory from a repo this recall isn't searching.
            var admissible = matches
                .Where(e => e.IsLatest
                    && e.Validity.ValidUntil is null
                    && scope.RepoIds.Contains(e.RepoId, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (admissible.Count == 0) return fused;

            return RecallScoring.ExpandEntities(fused, admissible, weights, now, parentTopK, maxCueMatches);
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
        var absTask = _store.SearchScoredAsync(SearchArm.Abstraction, repoIds, query, ct);
        await Task.WhenAll(lexTask, vecTask, absTask);

        var weights = RecallWeights.Default with
        {
            TotalN = ComputeTotalN(lexTask.Result, vecTask.Result, absTask.Result),
            Alpha = effectiveAlpha,
        };
        var fused = RecallScoring.Fuse(lexTask.Result, vecTask.Result, absTask.Result, weights, DateTime.UtcNow);

        var rows = fused
            .Select(c =>
            {
                var trust = MemoryTrust.Factor(c.Entry);
                return new RecallExplanationRow(
                    c.Entry.Id, c.Lex, c.Vec, c.Abs, c.Recency, c.Ucb, c.Fused, trust, c.Fused * trust);
            })
            .ToList();
        return new RecallExplanation(rows, weights.Alpha, rows.Count);
    }

    /// <summary>Σ (Echo+Fizzle) over the arm union (dedup by id) — the UCB exploration denominator base.</summary>
    private static long ComputeTotalN(params IReadOnlyList<ScoredHit>[] arms)
    {
        var seen = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var arm in arms)
        foreach (var hit in arm) seen.TryAdd(hit.Entry.Id, hit.Entry);
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

        // Wake-up slots are the scarcest surface in the system — 20 lines under a 600-token cap. Two
        // renderings of the same fact waste a slot outright, and near-duplicates are common here
        // because L1 shows one-liners: distinct memories routinely abstract to near-identical
        // sentences ("API reference table for X" / "API endpoint reference for X"). Dedup on what is
        // actually RENDERED rather than on the underlying content, since that is what the reader sees.
        var rendered = new List<string>(maxItems);

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
            if (rendered.Any(prior => WordSimilarity.Compute(prior, content) >= L1DuplicateThreshold))
                continue;

            var line = $"{typePrefix} {content}";

            var lineTokens = RecallScoring.EstimateTokens(line.Length);
            if (lineTokens > remainingTokens)
                break;

            rendered.Add(content);
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

    /// <summary>Service-wide enrichment backlog (all repos) — the /api/status signal.</summary>
    public Task<UnenrichedStats> GetUnenrichedStatsAsync(CancellationToken ct = default) =>
        _store.GetUnenrichedStatsAsync(null, ct);

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
        var missingTargets = await ResolveMissingCitationsAsync(entries, idSet, ct);
        var edges = new List<GraphEdge>();

        foreach (var e in entries)
        {
            foreach (var parentId in e.DerivedFrom)
            {
                // Emitted even when the target is unresolvable, flagged Missing. Dropping the edge made a
                // dangling citation render identically to no citation at all (#80) — the failure looked like
                // success. The canvas filters edges to visible node ids, so this is inert for the renderer
                // and informative to API consumers.
                edges.Add(new GraphEdge
                {
                    From = parentId,
                    To = e.Id,
                    Relation = "derived",
                    Status = missingTargets.Contains(parentId) ? GraphEdgeStatus.Missing : GraphEdgeStatus.Ok,
                });
            }
            foreach (var link in e.Links)
            {
                if (!string.IsNullOrEmpty(link.TargetMemoryId) && idSet.Contains(link.TargetMemoryId))
                    edges.Add(new GraphEdge { From = e.Id, To = link.TargetMemoryId, Relation = link.Relation });
            }
        }

        return new GraphData { Nodes = nodes, Edges = edges };
    }

    /// <summary>
    /// The cited ids that resolve to nothing at all — as distinct from the ones that merely fall outside
    /// this graph window. Conflating the two would flag most citations in any corpus larger than the window
    /// as dangling, which is exactly the false alarm that would train readers to ignore the flag.
    /// </summary>
    private async Task<HashSet<string>> ResolveMissingCitationsAsync(
        List<MemoryEntry> entries, HashSet<string> inWindow, CancellationToken ct)
    {
        var candidates = entries
            .SelectMany(e => e.DerivedFrom)
            .Where(id => !inWindow.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(CitationResolveCap)
            .ToList();

        // One batched round trip: this runs on a synchronous GET, and the cap is 200 — as a sequential
        // per-id loop it turned one request into up to 200 database round trips.
        var resolved = await _store.GetManyAsync(candidates, ct);
        return new HashSet<string>(
            resolved.Where(kv => kv.Value is null).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
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
        Stage = opts.Stage,
        Tags = opts.Tags?.ToList() ?? [],
        Limit = opts.Limit,
        IncludeExpired = opts.IncludeExpired,
        CrossRepo = opts.CrossRepo,
        ExpandGraph = opts.ExpandGraph,
        ExpandEntities = opts.ExpandEntities,
        AlphaOverride = opts.AlphaOverride,
    };

    // COMMON CASE — name the op once, give one HookContext factory. Pre gates, Post notifies,
    // cache invalidated in finally. Runner short-circuits internally when no hooks are enabled.
    private async Task<T> RunMutationAsync<T>(
        MutationKind kind, string scope,
        Func<HookContext> ctx,
        Func<MutationCtx, Task<T>> body,
        Func<string?, T> denied,
        CancellationToken ct)
    {
        var preResult = await _hooks.RunPreHooksAsync(kind.Pre(), WithEvent(kind.Pre(), ctx()), ct);
        if (!preResult.Allowed) return denied(preResult.Reason);

        try
        {
            return await RunWriteAsync(scope, body, ct);
        }
        finally
        {
            // Inner finally (RunWriteAsync) has already invalidated the scope, so the post-hook
            // fires after the generation bump. Fire-and-forget: never blocks or cancels with the caller.
            _ = _hooks.RunPostHooksAsync(kind.Post(), WithEvent(kind.Post(), ctx()), default);
        }
    }

    // ESCAPE HATCH — hookless writes (Feedback / Edit / Link). No event, no context, no denied.
    private async Task<T> RunWriteAsync<T>(
        string scope, Func<MutationCtx, Task<T>> body, CancellationToken ct)
    {
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
        }
    }

    private static HookContext WithEvent(HookEvent evt, HookContext ctx) =>
        new() { Event = EventName(evt), Repo = ctx.Repo, Data = ctx.Data };

    private static string EventName(HookEvent evt) => evt switch
    {
        HookEvent.PreStore => "pre-store",
        HookEvent.PostStore => "post-store",
        HookEvent.PreForget => "pre-forget",
        _ => "post-forget",
    };
}

/// <summary>
/// Storage write gate. The only code path with access to <see cref="IEidetStore"/>'s
/// write API from inside <see cref="MemoryService"/> — every mutation must accept a <c>MutationCtx</c>
/// from <c>RunWriteAsync</c> (directly or via <c>RunMutationAsync</c>) and call its methods, which
/// guarantees the cache invalidation in the surrounding <c>finally</c> block fires.
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
