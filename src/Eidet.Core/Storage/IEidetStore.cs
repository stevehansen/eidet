using Eidet.Core.Domain;

namespace Eidet.Core.Storage;

public interface IEidetStore
{
    Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default);
    Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default);
    Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<bool> ForgetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Patch the access-tracking fields (<c>AccessCount</c>, <c>LastAccessedAt</c>) without
    /// touching any other field. Cache-invariant safe: these fields are not in the recall
    /// cache key and do not affect recall scoring, so writes through this path do not
    /// invalidate the recall cache. The default implementation is a no-op so test fakes
    /// don't have to opt in unless they care about access tracking.
    /// </summary>
    Task PatchAccessAsync(string entryId, DateTime lastAccessedAt, CancellationToken ct = default) =>
        Task.CompletedTask;
    Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default);
    Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default);
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
