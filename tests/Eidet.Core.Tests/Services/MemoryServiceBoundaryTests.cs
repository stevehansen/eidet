using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Boundary tests for the deepened <see cref="MemoryService"/>: covers the structural
/// cache-coherence invariant that the friction doc named ("store-then-recall is an
/// implicit cross-object protocol — silent stale read"). Each test exercises only the
/// public surface; <c>RecallCache</c>, <c>RecallScoring</c>, <c>MutationCtx</c>, and
/// <c>AccessTrackingCtx</c> are internals that are never named directly here.
/// </summary>
public class MemoryServiceBoundaryTests
{
    [Fact]
    public async Task Store_then_Recall_returns_new_entry_even_after_warming_cache()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Warm the cache with an empty result for "auth" in repo-a.
        var first = await svc.RecallAsync("repo-a", "auth jwt rs256");
        Assert.Empty(first);

        // Store a matching memory.
        var stored = await svc.StoreAsync("repo-a",
            "Auth uses JWT RS256 with 10-minute TTL", MemoryType.Insight);
        Assert.True(stored.Success);

        // The next recall MUST observe the new entry — proves invalidation fired.
        var second = await svc.RecallAsync("repo-a", "auth jwt rs256");
        Assert.Single(second);
        Assert.Equal(stored.Id, second[0].Id);
    }

    [Fact]
    public async Task Forget_invalidates_cached_recall()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        var stored = await svc.StoreAsync("repo-a", "redis caching with 5-min ttl", MemoryType.Insight);
        Assert.True(stored.Success);

        // Populate cache.
        var before = await svc.RecallAsync("repo-a", "redis caching");
        Assert.Single(before);

        var forgotten = await svc.ForgetAsync(stored.Id!, reason: "outdated");
        Assert.True(forgotten);

        // After forget, the cache must be invalidated and the entry must not surface.
        var after = await svc.RecallAsync("repo-a", "redis caching");
        Assert.DoesNotContain(after, r => r.Id == stored.Id);
    }

    [Fact]
    public async Task Stores_to_different_repos_have_independent_cache_generations()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Warm cache for repo-a.
        var initialA = await svc.RecallAsync("repo-a", "deployment");
        Assert.Empty(initialA);

        // Store something in repo-b.
        await svc.StoreAsync("repo-b", "deployment uses kubernetes", MemoryType.Insight);

        // repo-a's cache should still be empty (no invalidation needed).
        // The IS NOT a stale-read concern because cross-repo defaults to true and the
        // per-scope invalidation tracks the right scopes — this test pins that
        // mutations to other scopes don't pollute the cache for unrelated scopes.
        var stillA = await svc.RecallAsync("repo-a", "deployment");
        Assert.Empty(stillA);

        // repo-b sees its own entry.
        var b = await svc.RecallAsync("repo-b", "deployment");
        Assert.Single(b);
    }

    [Fact]
    public async Task ValidatorRejection_does_not_invalidate_cache()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Warm cache.
        await svc.StoreAsync("repo-a", "JWT auth using RS256 keys", MemoryType.Insight);
        var beforeFirst = await svc.RecallAsync("repo-a", "jwt");
        Assert.Single(beforeFirst);

        // Attempt to store a secret — must be rejected.
        var blocked = await svc.StoreAsync("repo-a",
            "AWS access key: AKIAIOSFODNN7EXAMPLE", MemoryType.Observation);
        Assert.False(blocked.Success);
        Assert.Null(blocked.Id);

        // Cache is unchanged — recall still returns exactly the prior entry, no extra entries.
        var afterReject = await svc.RecallAsync("repo-a", "jwt");
        Assert.Single(afterReject);
    }

    [Fact]
    public async Task Bulk_invalidate_keeps_recall_coherent_after_direct_store_writes()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Warm cache.
        var initial = await svc.RecallAsync("repo-a", "deployment");
        Assert.Empty(initial);

        // Simulate a bulk-write path (e.g. ExportService.ImportPackAsync) that writes
        // directly to the store and then explicitly invalidates the recall cache via
        // the internal helper. This is exactly what the four tactical patches do.
        var entry = new MemoryEntry
        {
            Id = "memories/repo-a/insight/bulk-deploy-1",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "deployment uses argo cd via gitops",
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
            Importance = 0.7f,
        };
        await store.StoreAsync(entry);

        // Without invalidation, the next recall would hit the stale empty cache.
        // The internal helper is what bulk callers use; here we verify it works.
        typeof(MemoryService)
            .GetMethod("InvalidateRecallCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                types: [typeof(string)])!
            .Invoke(svc, ["repo-a"]);

        var after = await svc.RecallAsync("repo-a", "deployment");
        Assert.Contains(after, r => r.Id == entry.Id);
    }
}

/// <summary>
/// Minimal in-memory <see cref="IEidetStore"/> for boundary tests. Implements just enough
/// of the surface to exercise <see cref="MemoryService"/>'s public methods without
/// depending on RavenDB. Search is naive substring matching on Content + Tags + RepoId.
/// </summary>
internal class InMemoryEidetStore : IEidetStore
{
    private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public virtual Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _entries.TryGetValue(id, out var e);
            return Task.FromResult(e);
        }
    }

    public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        lock (_lock) _entries[entry.Id] = entry;
        return Task.FromResult(entry.Id);
    }

    public virtual Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        lock (_lock) _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task<bool> ForgetAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var e)) return Task.FromResult(false);
            e.Validity.ValidUntil = DateTime.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var results = _entries.Values
                .Where(e => repoIds.Contains(e.RepoId, StringComparer.OrdinalIgnoreCase))
                .Where(e => query.IncludeExpired || e.Validity.ValidUntil is null)
                .Where(e => terms.Any(t => e.Content.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Take(query.Limit * 2)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());

    public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) =>
        Task.FromResult<MemoryEntry?>(null);

    public virtual Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var counts = _entries.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.Type)
                .ToDictionary(g => g.Key, g => g.Count());
            return Task.FromResult(counts);
        }
    }

    public Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var results = _entries.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(e => types.Contains(e.Type))
                .Where(e => e.IsLatest && e.Validity.ValidUntil is null)
                .OrderByDescending(e => e.Importance)
                .Take(limit)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => Task.FromResult<DatabaseInfo?>(null);
    public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_entries.Values.Select(e => e.RepoId).Distinct().ToList());
        }
    }

    public Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var q = _entries.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.Validity.ValidUntil is null);
            if (type.HasValue) q = q.Where(e => e.Type == type.Value);
            return Task.FromResult(q.Skip(skip).Take(take).ToList());
        }
    }

    public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default) => Task.FromResult(layer.Id);
    public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default) => Task.FromResult(new List<MemoryLayer>());
    public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult<MemoryLayer?>(null);
    public Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
    public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_entries.Remove(id));
    }
}
