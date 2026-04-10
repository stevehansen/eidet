using Eidet.Core.Domain;
using Eidet.Core.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Eidet.Core.Storage;

public class RavenEidetStore : IEidetStore
{
    private const string EmbeddingsTaskId = "memory-embeddings";
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

    public async Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        if (entry.CreatedAt == default)
            entry.CreatedAt = DateTime.UtcNow;

        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(entry, entry.Id, ct);
        await session.SaveChangesAsync(ct);
        return entry.Id;
    }

    public async Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(entry, entry.Id, ct);
        await session.SaveChangesAsync(ct);
    }

    public async Task<bool> ForgetAsync(string id, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var entry = await session.LoadAsync<MemoryEntry>(id, ct);
        if (entry is null) return false;

        entry.Validity.ValidUntil = DateTime.UtcNow;
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<MemoryEntry>> FullTextSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var documentQuery = session.Advanced
            .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
            .Search("Content", query.Text)
            .WhereIn("RepoId", repoIds);

        documentQuery = ApplyFilters(documentQuery, query);
        return await documentQuery.Take(query.Limit * 2).ToListAsync(ct); // Over-fetch 2× for merge quality
    }

    public async Task<List<MemoryEntry>> VectorSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        try
        {
            using var session = _store.OpenAsyncSession();
            var documentQuery = session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .WhereIn("RepoId", repoIds)
                .VectorSearch(
                    field => field.WithField("ContentVector"),
                    searchTerm => searchTerm.ByText(query.Text, EmbeddingsTaskId),
                    minimumSimilarity: 0.70f,
                    numberOfCandidates: 30);

            documentQuery = ApplyFilters(documentQuery, query);
            return await documentQuery.Take(query.Limit).ToListAsync(ct);
        }
        catch
        {
            return []; // Vector search may fail if embeddings not configured
        }
    }

    public async Task<MemoryEntry?> FindDuplicateAsync(
        string repoId, string content, float threshold, CancellationToken ct = default)
    {
        // Strategy 1: Vector similarity
        try
        {
            using var session = _store.OpenAsyncSession();
            var results = await session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .WhereEquals("RepoId", repoId)
                .WhereEquals("ValidUntil", (DateTime?)null)
                .VectorSearch(
                    field => field.WithField("ContentVector"),
                    searchTerm => searchTerm.ByText(content, EmbeddingsTaskId),
                    minimumSimilarity: threshold,
                    numberOfCandidates: 10)
                .Take(1)
                .ToListAsync(ct);

            if (results.Count > 0)
                return results[0];
        }
        catch { }

        // Strategy 2: Full-text fallback with exact content match
        try
        {
            var searchSnippet = content.Length > 80 ? content[..80] : content;
            using var session = _store.OpenAsyncSession();
            var candidates = await session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .WhereEquals("RepoId", repoId)
                .WhereEquals("ValidUntil", (DateTime?)null)
                .Search("Content", searchSnippet)
                .Take(10)
                .ToListAsync(ct);

            return candidates.FirstOrDefault(c =>
                string.Equals(c.Content.Trim(), content.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public async Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(
        string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var counts = await session
            .Query<Memories_CountByType.Result, Memories_CountByType>()
            .Where(r => r.RepoId == repoId)
            .ToListAsync(ct);

        return counts.ToDictionary(c => c.Type, c => c.Count);
    }

    public async Task<List<MemoryEntry>> GetTopScoredAsync(
        string repoId, MemoryType[] types, int limit, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var results = await session.Advanced
            .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
            .WhereEquals("RepoId", repoId)
            .WhereEquals("ValidUntil", (DateTime?)null)
            .WhereIn("Type", types.Cast<object>())
            .OrderByDescending("Importance")
            .Take(limit)
            .ToListAsync(ct);

        // Filter to IsLatest client-side (not in the search index)
        return results.Where(e => e.IsLatest).ToList();
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
                    new GetBuildNumberOperation(), ct);
                serverVersion = buildNumber.FullVersion;
            }
            catch { }

            var indexExists = stats.Indexes.Any(i => i.Name == Memories_Search.IndexName_);

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

    public async Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default)
    {
        try
        {
            using var session = _store.OpenAsyncSession();
            var repoIds = await session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .SelectFields<RepoIdProjection>("RepoId")
                .Take(1000)
                .ToListAsync(ct);

            return repoIds
                .Select(r => r.RepoId)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private class RepoIdProjection { public string RepoId { get; set; } = ""; }

    // ─── Layer operations ─────────────────────────────────────────────

    public async Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(layer, layer.Id, ct);
        await session.SaveChangesAsync(ct);
        return layer.Id;
    }

    public async Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var layer = await session.LoadAsync<MemoryLayer>(layerId, ct);
        if (layer is null) return false;
        session.Delete(layer);
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var layers = await session.Query<MemoryLayer>()
            .ToListAsync(ct);

        // Filter to applicable layers: universal, or repo is in ApplicableRepos
        return layers.Where(l =>
            l.ApplicableRepos.Count == 0 ||
            l.ApplicableRepos.Contains(repoId, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public async Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<MemoryLayer>(layerId, ct);
    }

    private static IAsyncDocumentQuery<MemoryEntry> ApplyFilters(
        IAsyncDocumentQuery<MemoryEntry> documentQuery, MemoryQuery query)
    {
        if (query.Type.HasValue)
            documentQuery = documentQuery.WhereEquals("Type", query.Type.Value);

        foreach (var tag in query.Tags)
            documentQuery = documentQuery.WhereIn("Tags", new[] { tag });

        if (!query.IncludeExpired)
            documentQuery = documentQuery.WhereEquals("ValidUntil", (DateTime?)null);

        return documentQuery;
    }
}
