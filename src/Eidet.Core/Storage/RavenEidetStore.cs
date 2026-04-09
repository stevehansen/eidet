using Eidet.Core.Domain;
using Eidet.Core.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Eidet.Core.Storage;

public class RavenEidetStore : IEidetStore
{
    private readonly IDocumentStore _store;

    public RavenEidetStore(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<MemoryEntry>(id, ct);
    }

    public async Task StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(entry, entry.Id, ct);
        await session.SaveChangesAsync(ct);
    }

    public async Task<List<MemoryEntry>> SearchAsync(string repoId, string query, int limit = 20, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var results = await session.Query<MemoryEntry, Memories_Search>()
            .Where(e => e.RepoId == repoId)
            .Search(e => e.Content, query)
            .Take(limit)
            .ToListAsync(ct);
        return results;
    }

    public async Task<MemoryStats> GetStatsAsync(string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();

        var counts = await session.Query<MemoryEntry>()
            .Where(e => e.RepoId == repoId)
            .GroupBy(e => e.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byType = counts.ToDictionary(c => c.Type, c => c.Count);
        return new MemoryStats(
            TotalCount: byType.Values.Sum(),
            ObservationCount: byType.GetValueOrDefault(MemoryType.Observation),
            InsightCount: byType.GetValueOrDefault(MemoryType.Insight),
            ProcedureCount: byType.GetValueOrDefault(MemoryType.Procedure),
            HeuristicCount: byType.GetValueOrDefault(MemoryType.Heuristic));
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var operation = new GetDatabaseRecordOperation(_store.Database);
            var result = await _store.Maintenance.Server.SendAsync(operation, ct);
            return result != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var dbRecord = await _store.Maintenance.Server.SendAsync(
                new GetDatabaseRecordOperation(_store.Database), ct);
            if (dbRecord == null) return null;

            var stats = await _store.Maintenance.SendAsync(
                new Raven.Client.Documents.Operations.GetStatisticsOperation(), ct);

            var serverVersion = "unknown";
            try
            {
                var buildNumber = await _store.Maintenance.Server.SendAsync(
                    new Raven.Client.ServerWide.Operations.GetBuildNumberOperation(), ct);
                serverVersion = buildNumber.FullVersion;
            }
            catch { }

            var indexExists = stats.Indexes.Any(i => i.Name == new Memories_Search().IndexName);

            return new DatabaseInfo(
                Name: _store.Database,
                ServerVersion: serverVersion,
                DocumentCount: stats.CountOfDocuments,
                IndexExists: indexExists);
        }
        catch
        {
            return null;
        }
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await IndexCreation.CreateIndexesAsync(
            typeof(Memories_Search).Assembly, _store, token: ct);
    }
}
