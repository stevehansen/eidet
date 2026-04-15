using Eidet.Core.Domain;

namespace Eidet.Core.Storage;

public interface IEidetStore
{
    Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default);
    Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default);
    Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<bool> ForgetAsync(string id, CancellationToken ct = default);
    Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default);
    Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default);
    Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default);
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
