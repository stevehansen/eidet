using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Memory;

/// <summary>
/// Lookup operations that don't go through the recall pipeline: version chains,
/// store/type counts, paged browse, distinct repo ids, and graph projection.
/// All read-only — no cache interaction, no hooks.
/// </summary>
internal sealed class MemoryQueries
{
    private readonly IEidetStore _store;

    public MemoryQueries(IEidetStore store)
    {
        _store = store;
    }

    public async Task<List<MemoryEntry>> GetVersionChainAsync(string memoryId, CancellationToken ct)
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

    public Task<DatabaseInfo?> GetStoreInfoAsync(CancellationToken ct) =>
        _store.GetDatabaseInfoAsync(ct);

    public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct) =>
        _store.GetCountsByTypeAsync(repoId, ct);

    public Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type, CancellationToken ct) =>
        _store.BrowseAsync(RepoIdNormalizer.Normalize(repoId), skip, take, type, ct);

    public Task<List<string>> GetRepoIdsAsync(CancellationToken ct) =>
        _store.GetDistinctRepoIdsAsync(ct);

    public async Task<GraphData> GetGraphDataAsync(string repoId, int limit, CancellationToken ct)
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
}
