using Eidet.Core.Domain;

namespace Eidet.Core.Storage;

public interface IEidetStore
{
    Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default);
    Task StoreAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<List<MemoryEntry>> SearchAsync(string repoId, string query, int limit = 20, CancellationToken ct = default);
    Task<MemoryStats> GetStatsAsync(string repoId, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
    Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default);
    Task EnsureIndexesAsync(CancellationToken ct = default);
}

public record MemoryStats(
    int TotalCount,
    int ObservationCount,
    int InsightCount,
    int ProcedureCount,
    int HeuristicCount);

public record DatabaseInfo(
    string Name,
    string ServerVersion,
    long DocumentCount,
    bool IndexExists);
