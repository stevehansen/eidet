using Eidet.Core.Domain;

namespace Eidet.Core.Storage;

/// <summary>One hit from a single search arm, carrying that arm's raw relevance score.</summary>
public readonly record struct ScoredHit(MemoryEntry Entry, double Score);

/// <summary>The two arms of hybrid search — lexical (full-text) and semantic (vector).</summary>
public enum SearchArm { Lexical, Vector }

public interface IEidetStore
{
    Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default);
    Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default);
    Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<bool> ForgetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Patch the access-tracking fields (<c>AccessCount</c>, <c>LastAccessedAt</c>) without
    /// touching any other field. These fields are not in the recall cache key, so writes through
    /// this path do not invalidate the recall cache; <c>LastAccessedAt</c> does feed dual-clock
    /// recency, so a cached recall may be marginally stale on recency — bounded by the short cache
    /// TTL. The default implementation is a no-op so test fakes don't have to opt in unless they
    /// care about access tracking.
    /// </summary>
    Task PatchAccessAsync(string entryId, DateTime lastAccessedAt, CancellationToken ct = default) =>
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
