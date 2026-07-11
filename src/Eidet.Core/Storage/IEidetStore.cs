using Eidet.Core.Domain;

namespace Eidet.Core.Storage;

/// <summary>One hit from a single search arm, carrying that arm's raw relevance score.</summary>
public readonly record struct ScoredHit(MemoryEntry Entry, double Score);

/// <summary>The two arms of hybrid search — lexical (full-text) and semantic (vector).</summary>
public enum SearchArm { Lexical, Vector }

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
    /// produce a sensible ordering without opting in.
    /// </summary>
    Task<IReadOnlyList<ScoredHit>> SearchScoredAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        SearchScoredViaRankDecayAsync(arm, repoIds, query, ct);

    /// <summary>Shared rank-decay fallback used by the default impl and by arms that can't surface a per-hit score.</summary>
    private async Task<IReadOnlyList<ScoredHit>> SearchScoredViaRankDecayAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct)
    {
        var entries = arm == SearchArm.Lexical
            ? await FullTextSearchAsync(repoIds, query, ct)
            : await VectorSearchAsync(repoIds, query, ct);
        return entries.Select((e, rank) => new ScoredHit(e, 1.0 / (rank + 1))).ToList();
    }

    Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default);

    /// <summary>
    /// Near-duplicate candidates of <paramref name="entry"/> within the same repo, ranked by
    /// semantic similarity, filtered server-side to those at or above <paramref name="minSimilarity"/>.
    /// Excludes the entry itself and anything not latest/valid. Returns [] when embeddings are
    /// unavailable (caller falls back to lexical matching). Default no-op for fakes that don't index vectors.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
    Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default);
    Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default);
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
