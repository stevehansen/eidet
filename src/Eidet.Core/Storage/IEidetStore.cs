using Eidet.Core.Domain;

namespace Eidet.Core.Storage;

/// <summary>One hit from a single search arm, carrying that arm's raw relevance score.</summary>
public readonly record struct ScoredHit(MemoryEntry Entry, double Score);

/// <summary>
/// The arms of hybrid search — lexical (full-text), semantic (vector over the whole entry), and
/// abstraction (vector over the entry's one-line self-description alone). The first two answer
/// "what does this memory contain"; the third answers "what is this memory ABOUT", which a long
/// body would otherwise outvote in the composite embedding.
/// </summary>
public enum SearchArm { Lexical, Vector, Abstraction }

/// <summary>
/// One EWMA step for a repo's learned lexical-vs-vector alpha. The new value is computed
/// <i>server-side</i> from the document's current alpha so concurrent feedback can't lose an update:
/// <c>next = clamp((1-Lambda)·(current ?? Fallback) + Lambda·Target, Min, Max)</c>. The caller supplies
/// only the relevance label (<see cref="Target"/>) and the recall-domain constants; the store owns the
/// atomic apply.
/// </summary>
public readonly record struct AlphaEwmaUpdate(double Target, double Lambda, double Min, double Max, double Fallback);

public interface IEidetStore
{
    Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Resolves several memories in one round trip. EVERY requested id is present as a key, with a null
    /// value for "no such document" — the citation checks need to tell a missing target apart from one
    /// that resolved, and an id merely absent from the result would conflate the two. Ids are matched
    /// case-insensitively, as RavenDB matches them.
    /// The default implementation loops <see cref="GetAsync"/> so fakes need not opt in; the point of
    /// overriding it is the round trip, not the semantics.
    /// </summary>
    async Task<IReadOnlyDictionary<string, MemoryEntry?>> GetManyAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, MemoryEntry?>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            resolved[id] = await GetAsync(id, ct);
        }
        return resolved;
    }

    Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default);
    Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<bool> ForgetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Patch the access-tracking fields (<c>AccessCount</c>, <c>LastAccessedAt</c>, and — when
    /// <paramref name="lexShare"/> is supplied — <c>LastLexShare</c>) without touching any other field.
    /// These fields are not in the recall cache key, so writes through this path do not invalidate the
    /// recall cache; <c>LastAccessedAt</c> does feed dual-clock recency, so a cached recall may be
    /// marginally stale on recency — bounded by the short cache TTL. The default implementation is a
    /// no-op so test fakes don't have to opt in unless they care about access tracking.
    /// </summary>
    Task PatchAccessAsync(string entryId, DateTime lastAccessedAt, double? lexShare = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// The per-repo learned lexical-vs-vector blend weight (<c>RepoUsage.AlphaLex</c>), or null when
    /// unlearned (caller falls back to <c>RecallWeights.Default.Alpha</c>). Default null so fakes need
    /// not opt in.
    /// </summary>
    Task<double?> GetRepoAlphaAsync(string repoId, CancellationToken ct = default) =>
        Task.FromResult<double?>(null);

    /// <summary>
    /// Apply one EWMA step to the per-repo alpha (and bump the sample count) on the repo's usage anchor,
    /// computing the new value server-side from the stored alpha so concurrent feedback can't lose an
    /// update. Never disturbs the anchor's usage time series or original-path mapping. Default no-op for fakes.
    /// </summary>
    Task UpdateRepoAlphaAsync(string repoId, AlphaEwmaUpdate update, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// The Reflector's coverage cursor (<c>RepoUsage.LastReflectedAt</c>): the UTC instant of the last
    /// live reflection run for a repo, or null if never reflected. Residue mining only considers signal
    /// newer than this so nightly runs walk forward without re-minting from the same feedback. Default
    /// null so fakes need not opt in.
    /// </summary>
    Task<DateTime?> GetLastReflectedAtAsync(string repoId, CancellationToken ct = default) =>
        Task.FromResult<DateTime?>(null);

    /// <summary>
    /// Advance the reflection cursor on the repo's usage anchor (JS-patch like
    /// <see cref="UpdateRepoAlphaAsync"/>, never disturbing the usage time series or original-path
    /// mapping). Default no-op for fakes.
    /// </summary>
    Task SetLastReflectedAtAsync(string repoId, DateTime whenUtc, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// The git-intake watermark (<c>RepoUsage.GitIntakeLastSha</c>): SHA of the repo tip at the
    /// end of the last non-dry git-history intake run, or null if never run. Drives the
    /// incremental <c>Since</c> default. Default null so fakes need not opt in.
    /// </summary>
    Task<string?> GetGitIntakeWatermarkAsync(string repoId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Advance the git-intake watermark on the repo's usage anchor (JS-patch like
    /// <see cref="SetLastReflectedAtAsync"/>, never disturbing the usage time series or
    /// original-path mapping). Last-write-wins — SHAs carry no order to guard monotonically.
    /// Default no-op for fakes.
    /// </summary>
    Task SetGitIntakeWatermarkAsync(string repoId, string sha, CancellationToken ct = default) =>
        Task.CompletedTask;
    Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default);
    Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default);

    /// <summary>
    /// One arm of hybrid search, ranked, with the backend's raw relevance surfaced as
    /// <see cref="ScoredHit.Score"/> (RavenDB's lexical <c>@index-score</c> for the lexical arm,
    /// vector similarity for the vector arm). The caller fuses the two arms.
    /// Vector arm returns [] when embeddings are unconfigured (caller degrades to lexical-only).
    /// The default implementation delegates to the arm's existing entity method and assigns a
    /// rank-decay score (<c>1, 1/2, 1/3, …</c>) so fakes that don't surface real scores still
    /// produce a sensible ordering without opting in. <see cref="SearchArm.Abstraction"/> has no
    /// entity-method equivalent to fall back on, so it defaults to <c>[]</c> — an arm that returns
    /// nothing contributes 0 to every candidate, which is exactly "this store has no abstraction
    /// index" and leaves a fake's ranking bit-identical to its two-arm behavior.
    /// </summary>
    Task<IReadOnlyList<ScoredHit>> SearchScoredAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        arm == SearchArm.Abstraction
            ? Task.FromResult<IReadOnlyList<ScoredHit>>([])
            : SearchScoredViaRankDecayAsync(arm, repoIds, query, ct);

    /// <summary>Shared rank-decay fallback used by the default impl and by arms that can't surface a per-hit score.</summary>
    private async Task<IReadOnlyList<ScoredHit>> SearchScoredViaRankDecayAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct)
    {
        var entries = arm == SearchArm.Lexical
            ? await FullTextSearchAsync(repoIds, query, ct)
            : await VectorSearchAsync(repoIds, query, ct);
        return entries.Select((e, rank) => new ScoredHit(e, 1.0 / (rank + 1))).ToList();
    }

    /// <summary>
    /// Memories sharing at least one of <paramref name="entities"/> — the cue-anchor lookup behind
    /// entity expansion. Latest + valid only, capped at <paramref name="max"/>, excluding
    /// <paramref name="excludeIds"/> (the candidates that produced the cues). Scope is the caller's
    /// <paramref name="repoIds"/>; the caller still re-checks each entry's real repo before admitting it.
    /// Default <c>[]</c> so fakes need not opt in — no cue index means no expansion, not an error.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> FindByEntitiesAsync(
        IReadOnlyList<string> repoIds, IReadOnlyCollection<string> entities,
        IReadOnlyCollection<string> excludeIds, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="FindDuplicateAsync"/> but restricted to one <paramref name="type"/>.
    ///
    /// Exists for producers that emit content byte-identical to their own input — consolidation
    /// folds observations into an insight whose body, absent LLM polish, IS the representative
    /// observation's body. Asking the type-agnostic question there always answers with the source,
    /// so a caller checking "have I emitted this yet" reads done as not-done and re-emits on every
    /// scheduled run. Scoping the probe to the OUTPUT type is what makes the answer meaningful.
    ///
    /// The default filters the type-agnostic result, which is correct whenever the nearest match is
    /// the answer; <see cref="RavenEidetStore"/> overrides it to push the filter into the query so a
    /// same-content source cannot crowd out the real hit.
    /// </summary>
    async Task<MemoryEntry?> FindDuplicateOfTypeAsync(
        string repoId, MemoryType type, string content, float threshold, CancellationToken ct = default)
    {
        var hit = await FindDuplicateAsync(repoId, content, threshold, ct);
        return hit?.Type == type ? hit : null;
    }

    /// <summary>
    /// Near-duplicate candidates of <paramref name="entry"/> within the same repo, ranked by
    /// semantic similarity, filtered server-side to those at or above <paramref name="minSimilarity"/>.
    /// Excludes the entry itself and anything not latest/valid. Returns [] when embeddings are
    /// unavailable (caller falls back to lexical matching). Default no-op for fakes that don't index vectors.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
    /// <summary>
    /// The recently soft-deleted memories for a repo — those forgotten (<c>Validity.ValidUntil</c> set)
    /// or superseded (<c>IsLatest == false</c>), newest-invalidated first, capped at <paramref name="max"/>.
    /// The post-forget integrity auditor's input set; every one of these must be invisible to every read
    /// path. Default <c>[]</c> so fakes need not opt in.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> GetInvalidatedAsync(string repoId, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    /// <summary>
    /// Live memories whose provenance was never established — the field is absent (a document written
    /// before it existed) or stored as <c>Unknown</c> — and whose <c>Source</c> is one of
    /// <paramref name="repairableSources"/>. OLDEST first.
    ///
    /// Both halves of that contract exist to make the nightly repair's progress monotone, and neither is
    /// incidental. Oldest-first, because every other read path in the system samples newest-first and the
    /// documents needing repair are by definition the oldest. Filtered to repairable sources, because a
    /// memory this build cannot derive a provenance for would otherwise sit at the head of that queue
    /// every night and starve the ones it can fix. Default <c>[]</c> so fakes need not opt in.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> GetUnprovenancedAsync(
        string repoId, IReadOnlyCollection<string> repairableSources, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default);
    Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default);

    /// <summary>
    /// Every source id ever folded into a derived memory (insight or procedure) for this repo —
    /// including the lineage of memories that have since been retired, superseded, or repaired away.
    ///
    /// Deliberately blind to validity, because lineage is a historical fact and consolidation's
    /// idempotence depends on it staying one. Reading lineage off *live* memories only makes any
    /// stage that retires a consolidation output (corpus repair on an exact-content duplicate, dedup
    /// merging two insights, TTL expiry) also erase the evidence that its cluster was already
    /// consolidated — so the next scheduled run reads the cluster as fresh and emits it again, on a
    /// loop bounded only by how often the two stages run.
    ///
    /// Default <c>[]</c> so fakes need not opt in; callers union this with their own live scan.
    /// </summary>
    Task<HashSet<string>> GetConsolidatedSourceIdsAsync(string repoId, CancellationToken ct = default) =>
        Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Memories still awaiting enrichment (<c>Summary == null</c>), valid + latest only, oldest
    /// first. The retry feed for the nightly enrichment sweep: the EnrichmentWorker's subscription
    /// acks a doc even when enrichment fails and never re-sends it, so everything the worker missed
    /// must be re-selectable here. Default <c>[]</c> so fakes need not opt in.
    /// </summary>
    Task<List<MemoryEntry>> GetUnenrichedAsync(string repoId, int limit, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());

    /// <summary>
    /// Count and oldest <c>CreatedAt</c> of memories awaiting enrichment — across all repos when
    /// <paramref name="repoId"/> is null. The backlog signal for <c>/api/status</c>: a non-null
    /// oldest that keeps aging means something is stuck. Default zero so fakes need not opt in.
    /// </summary>
    Task<UnenrichedStats> GetUnenrichedStatsAsync(string? repoId = null, CancellationToken ct = default) =>
        Task.FromResult(new UnenrichedStats(0, null));

    Task<bool> TestConnectionAsync(CancellationToken ct = default);
    Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default);
    Task EnsureIndexesAsync(CancellationToken ct = default);

    Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default);
    Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default);

    // Layer operations
    Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default);
    Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default);
    Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default);
    Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default);
    Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default);
    Task<bool> HardDeleteAsync(string id, CancellationToken ct = default);
}

public record DatabaseInfo(
    string Name,
    string ServerVersion,
    long DocumentCount,
    bool IndexExists);

/// <summary>Enrichment backlog: how many memories lack a Summary, and how old the oldest is.</summary>
public record UnenrichedStats(int Count, DateTime? OldestCreatedAt);
