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

    // ─── Bulk-write gate (#10) ──────────────────────────────────────

    private static MemoryEntry MakeEntry(string repoId, string id, string content) => new()
    {
        Id = id,
        RepoId = repoId,
        Type = MemoryType.Insight,
        Content = content,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = 0.7f,
    };

    [Fact]
    public async Task RunBulkAsync_StoresMany_InvalidatesEachScopeOnce()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Warm an empty cache for every scope the bulk will touch.
        Assert.Empty(await svc.RecallAsync("repo-a", "kubernetes"));
        Assert.Empty(await svc.RecallAsync("repo-b", "kubernetes"));
        Assert.Empty(await svc.RecallAsync("repo-c", "kubernetes"));

        await svc.RunBulkAsync(async ctx =>
        {
            await ctx.StoreNewAsync(MakeEntry("repo-a", "memories/repo-a/insight/1", "deploys to kubernetes via argo"), CancellationToken.None);
            await ctx.StoreNewAsync(MakeEntry("repo-a", "memories/repo-a/insight/2", "kubernetes ingress is nginx"), CancellationToken.None);
            await ctx.StoreNewAsync(MakeEntry("repo-b", "memories/repo-b/insight/1", "kubernetes cluster is gke"), CancellationToken.None);
            await ctx.StoreNewAsync(MakeEntry("repo-c", "memories/repo-c/insight/1", "kubernetes nodes autoscale"), CancellationToken.None);
            return 0;
        });

        // Each touched scope must observe its own new entries after the bulk. The "exactly
        // once" bump count isn't observable through the public API; per-scope coherence is
        // the strongest invariant we can assert from here.
        Assert.Equal(2, (await svc.RecallAsync("repo-a", "kubernetes")).Count);
        Assert.Single(await svc.RecallAsync("repo-b", "kubernetes"));
        Assert.Single(await svc.RecallAsync("repo-c", "kubernetes"));
    }

    [Fact]
    public async Task RunBulkAsync_BodyThrows_StillInvalidatesTouchedScopes()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Warm repo-a's empty cache so a stale read would be observable.
        Assert.Empty(await svc.RecallAsync("repo-a", "kubernetes"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RunBulkAsync<int>(async ctx =>
            {
                await ctx.StoreNewAsync(MakeEntry("repo-a", "memories/repo-a/insight/1", "deploys to kubernetes"), CancellationToken.None);
                throw new InvalidOperationException("boom");
            }));

        // The finally must have invalidated repo-a despite the throw — the entry written
        // before the exception is now visible rather than masked by the stale empty cache.
        Assert.Single(await svc.RecallAsync("repo-a", "kubernetes"));
    }

    [Fact]
    public async Task RunBulkAsync_HardDelete_UsesExplicitScope()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Entry living in scope "layer-x" — the scope we will pass to HardDeleteAsync.
        var inLayer = MakeEntry("layer-x", "memories/layer-x/insight/1", "redis caching layer");
        await store.StoreAsync(inLayer);
        // Entry living in repo-a — its RepoId differs from the scope we will pass.
        var inRepoA = MakeEntry("repo-a", "memories/repo-a/insight/1", "redis caching repo");
        await store.StoreAsync(inRepoA);

        // Warm both caches so a missed invalidation is observable as a stale hit.
        Assert.Single(await svc.RecallAsync("layer-x", "redis"));
        Assert.Single(await svc.RecallAsync("repo-a", "redis"));

        // Delete BOTH entries but always pass the explicit scope "layer-x".
        await svc.RunBulkAsync(async ctx =>
        {
            await ctx.HardDeleteAsync(inLayer.Id, "layer-x", CancellationToken.None);
            await ctx.HardDeleteAsync(inRepoA.Id, "layer-x", CancellationToken.None);
            return 0;
        });

        // Both entries are physically gone from the store.
        Assert.Null(await store.GetAsync(inLayer.Id));
        Assert.Null(await store.GetAsync(inRepoA.Id));

        // The explicit scope "layer-x" was invalidated: its recall reflects the deletion.
        Assert.Empty(await svc.RecallAsync("layer-x", "redis"));

        // repo-a's scope was NOT invalidated — proving the SCOPE PARAMETER ("layer-x"), not
        // the deleted entry's RepoId ("repo-a"), is what gets recorded. repo-a still serves
        // its warmed (now physically stale) cache, so the recall still returns the entry.
        Assert.Single(await svc.RecallAsync("repo-a", "redis"));
    }

    [Fact]
    public async Task RunBulkAsync_WithFireHooks_FiresPerEntry()
    {
        var store = new InMemoryEidetStore();
        var hooks = new RecordingHookRunner();
        var svc = new MemoryService(store, hooks: hooks);

        await svc.RunBulkAsync(async ctx =>
        {
            await ctx.StoreNewAsync(MakeEntry("repo-a", "memories/repo-a/insight/1", "one"), CancellationToken.None);
            await ctx.StoreNewAsync(MakeEntry("repo-a", "memories/repo-a/insight/2", "two"), CancellationToken.None);
            await ctx.StoreNewAsync(MakeEntry("repo-a", "memories/repo-a/insight/3", "three"), CancellationToken.None);
            return 0;
        }, new BulkOptions { FireHooks = true });
        await hooks.Drain();

        Assert.Equal(3, hooks.Fired.Count(e => e == HookEvent.PostStore));

        // Default options (FireHooks = false) must fire no post-store hooks.
        var hooks2 = new RecordingHookRunner();
        var svc2 = new MemoryService(store, hooks: hooks2);
        await svc2.RunBulkAsync(async ctx =>
        {
            await ctx.StoreNewAsync(MakeEntry("repo-b", "memories/repo-b/insight/1", "one"), CancellationToken.None);
            await ctx.StoreNewAsync(MakeEntry("repo-b", "memories/repo-b/insight/2", "two"), CancellationToken.None);
            return 0;
        });
        await hooks2.Drain();

        Assert.Empty(hooks2.Fired);
    }

    [Fact]
    public async Task RunBulkAsync_WithValidate_RejectsBadEntry_FailsFast()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Warm repo-a so we can observe whether the pre-throw good store landed + invalidated.
        Assert.Empty(await svc.RecallAsync("repo-a", "deployment"));

        var good = MakeEntry("repo-a", "memories/repo-a/insight/good", "deployment uses argo cd");
        var bad = MakeEntry("repo-a", "memories/repo-a/observation/bad", "AWS access key: AKIAIOSFODNN7EXAMPLE");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RunBulkAsync<int>(async ctx =>
            {
                await ctx.StoreNewAsync(good, CancellationToken.None);
                await ctx.StoreNewAsync(bad, CancellationToken.None); // secret → fail-fast
                return 0;
            }, new BulkOptions { Validate = true }));

        // Fail-fast does not roll back: the good entry stored before the bad one persists,
        // and the finally invalidated repo-a so the recall sees it.
        Assert.Single(await svc.RecallAsync("repo-a", "deployment"));
        // The rejected entry was never stored.
        Assert.Null(await store.GetAsync(bad.Id));
    }

    [Fact]
    public async Task WriteManyAsync_SkipIfExists_SkipsExisting()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        var a = MakeEntry("repo-a", "memories/repo-a/insight/a", "alpha deployment notes");
        var b = MakeEntry("repo-a", "memories/repo-a/insight/b", "bravo deployment notes");
        await store.StoreAsync(a);

        var result = await svc.WriteManyAsync([a, b], new BulkWriteOptions { SkipIfExists = true });
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);

        // B is now searchable, A is still present — recall sees both.
        Assert.Equal(2, (await svc.RecallAsync("repo-a", "deployment")).Count);

        // Without SkipIfExists, a pre-existing id is overwritten rather than skipped.
        var store2 = new InMemoryEidetStore();
        var svc2 = new MemoryService(store2);
        var a2 = MakeEntry("repo-b", "memories/repo-b/insight/a", "alpha");
        var b2 = MakeEntry("repo-b", "memories/repo-b/insight/b", "bravo");
        await store2.StoreAsync(a2);
        var result2 = await svc2.WriteManyAsync([a2, b2]);
        Assert.Equal(2, result2.Added);
        Assert.Equal(0, result2.Skipped);
    }

    [Fact]
    public async Task UpdateManyAsync_InvalidatesOncePerScope()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        var e1 = MakeEntry("repo-a", "memories/repo-a/insight/1", "original alpha");
        var e2 = MakeEntry("repo-a", "memories/repo-a/insight/2", "original bravo");
        await store.StoreAsync(e1);
        await store.StoreAsync(e2);

        // Warm repo-a; "rewritten" matches nothing yet.
        Assert.Empty(await svc.RecallAsync("repo-a", "rewritten"));

        e1.Content = "rewritten alpha";
        e2.Content = "rewritten bravo";
        var written = await svc.UpdateManyAsync([e1, e2]);
        Assert.Equal(2, written);

        // repo-a's cache was invalidated — the updated content is now observable.
        Assert.Equal(2, (await svc.RecallAsync("repo-a", "rewritten")).Count);
    }

    [Fact]
    public async Task Concurrent_StoreDuringRecall_NoStaleResult()
    {
        // The seam the friction doc named: a recall that snapshots an empty result, then a
        // store lands (bumping the scope generation) before the recall writes its result to
        // the cache. The recall must NOT poison the cache with the now-stale empty result.
        var store = new GatedSearchStore();
        var svc = new MemoryService(store);

        // R1 enters the store query — having already snapshotted the scope generation — and
        // blocks there, holding the pre-store (empty) result.
        var recallTask = svc.RecallAsync("repo-a", "deployment");
        await store.SearchEntered;

        // A concurrent store completes fully (including the cache-generation bump in
        // RunMutationAsync's finally) while R1 is still in flight.
        var stored = await svc.StoreAsync("repo-a", "deployment uses kubernetes", MemoryType.Insight);
        Assert.True(stored.Success);

        // Release R1. It returns the empty snapshot it legitimately observed pre-store; its
        // attempt to cache that result must be discarded because the generation moved.
        store.ReleaseSearch();
        var r1 = await recallTask;
        Assert.Empty(r1);

        // R2 must observe the concurrently-stored entry — proof that R1 did not serve a
        // stale empty result from the cache for the TTL window.
        var r2 = await svc.RecallAsync("repo-a", "deployment");
        Assert.Single(r2);
        Assert.Equal(stored.Id, r2[0].Id);
    }
}

/// <summary>
/// In-memory store that blocks the first full-text search after it has read the store,
/// letting a test interleave a concurrent store between a recall's generation snapshot
/// and its cache write. Later searches run unblocked.
/// </summary>
internal sealed class GatedSearchStore : InMemoryEidetStore
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public Task SearchEntered => _entered.Task;
    public void ReleaseSearch() => _released.TrySetResult();

    public override async Task<List<MemoryEntry>> FullTextSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        // Capture the result against the current store state, THEN gate — so the blocked
        // recall returns the pre-store snapshot rather than data written while it waited.
        var snapshot = await base.FullTextSearchAsync(repoIds, query, ct);
        if (Interlocked.Increment(ref _calls) == 1)
        {
            _entered.TrySetResult();
            await _released.Task;
        }
        return snapshot;
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
    private readonly Dictionary<string, DateTime> _reflectionCursors = new(StringComparer.OrdinalIgnoreCase);
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

    public virtual Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var results = _entries.Values
                .Where(e => repoIds.Contains(e.RepoId, StringComparer.OrdinalIgnoreCase))
                .Where(e => query.IncludeExpired || e.Validity.ValidUntil is null)
                // Mirror RavenEidetStore.ApplyFilters' None-as-wildcard stage filter so recall
                // semantics can be exercised without RavenDB.
                .Where(e => query.Stage is not { } s || e.Stage == s || e.Stage == FunctionalStage.None)
                .Where(e => terms.Any(t => e.Content.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Take(query.Limit * 2)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public virtual Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());

    public virtual Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) =>
        Task.FromResult<MemoryEntry?>(null);

    // Reflection coverage cursor — real backing store (default interface impl is null/no-op). Purely
    // additive: only the Reflector tests read/advance it; every existing test is unaffected.
    public Task<DateTime?> GetLastReflectedAtAsync(string repoId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_reflectionCursors.TryGetValue(repoId, out var t) ? t : (DateTime?)null);
    }

    public Task SetLastReflectedAtAsync(string repoId, DateTime whenUtc, CancellationToken ct = default)
    {
        lock (_lock) _reflectionCursors[repoId] = whenUtc;
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    // Soft-deleted set for the post-forget integrity auditor: forgotten (ValidUntil set) or superseded
    // (IsLatest false). Virtual so a leak-store fake can widen the read paths that expose them.
    public virtual Task<IReadOnlyList<MemoryEntry>> GetInvalidatedAsync(string repoId, int max, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var r = _entries.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.Validity.ValidUntil is not null || !e.IsLatest)
                .Take(max)
                .ToList();
            return Task.FromResult<IReadOnlyList<MemoryEntry>>(r);
        }
    }

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

    public virtual Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default)
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
