using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Tests for graph-neighbor expansion (issue #33 item 7). Two surfaces:
///
/// Section A — the pure <see cref="RecallScoring.ExpandNeighbors"/> spreading-activation math: a fused
/// candidate's links pull off-pool neighbors into the pool via damped inheritance
/// (<c>parentFused·decay + neighborOwn(ucb+recency)</c>), bounded to one hop and a max-neighbor cap,
/// deduped, re-sorted.
///
/// Section B — the <see cref="MemoryService.ExpandNeighborsAsync"/> I/O wrapper through RecallAsync:
/// a gold memory in NEITHER arm but linked from a top hit surfaces when ExpandGraph is on and is absent
/// when off; the loaded entry's REAL RepoId (not the link's declared TargetRepoId) gates scope; and a
/// failing/missing/non-latest neighbor load never fails recall.
/// </summary>
public class GraphExpansionTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryEntry Entry(
        string id, string repoId = "repo-a", bool isLatest = true,
        params (string repo, string target)[] links)
    {
        var e = new MemoryEntry
        {
            Id = id,
            RepoId = repoId,
            Type = MemoryType.Insight,
            Content = id,
            CreatedAt = Now,
            Validity = new Validity { ValidFrom = Now },
            IsLatest = isLatest,
            Importance = 0.5f,
        };
        foreach (var (repo, target) in links)
            e.Links.Add(new MemoryLink { TargetRepoId = repo, TargetMemoryId = target, Relation = "supports" });
        return e;
    }

    private static FusedCandidate Candidate(MemoryEntry entry, double fused, double lex = 0, double vec = 0) =>
        new(entry, lex, vec, Recency: 0, Ucb: 0, Fused: fused);

    // ════════════════════════════════════════════════════════════════════════
    // Section A — pure ExpandNeighbors math
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A1 — a parent linking an off-pool neighbor yields that neighbor with Fused ≈
    /// parentFused·0.5 + neighbor(ucb+recency), Lex==0, Vec==0; output re-sorted descending.
    /// </summary>
    [Fact]
    public void ExpandNeighbors_LinkedNeighbor_InheritsDampedParentScore()
    {
        var parent = Entry("parent", links: ("repo-a", "neighbor"));
        var neighbor = Entry("neighbor");
        var fused = new List<FusedCandidate> { Candidate(parent, fused: 0.8) };

        var resolved = new Dictionary<string, MemoryEntry> { ["neighbor"] = neighbor };
        var expanded = RecallScoring.ExpandNeighbors(
            fused, id => resolved.GetValueOrDefault(id), RecallWeights.Default, Now);

        var n = expanded.Single(c => c.Entry.Id == "neighbor");
        Assert.Equal(0.0, n.Lex);
        Assert.Equal(0.0, n.Vec);

        // The neighbor's own recency+ucb computed the same way the helper does, plus the damped parent.
        var ucb = 0.0; // TotalN=0 ⇒ ln(1)=0 ⇒ ucb term 0
        var recency = FadeMemCurve.Recency(neighbor.CreatedAt, neighbor.LastAccessedAt, Now, neighbor.Type);
        Assert.Equal(0.8 * 0.5 + ucb + recency, n.Fused, precision: 9);

        // Output is re-sorted by fused descending (the neighbor's own fresh recency happens to lift it
        // above the parent's bare 0.8 here — the contract under test is the ordering, not which id wins).
        Assert.Equal(2, expanded.Count);
        for (var i = 1; i < expanded.Count; i++)
            Assert.True(expanded[i - 1].Fused >= expanded[i].Fused,
                $"expanded must be sorted descending: [{i - 1}]={expanded[i - 1].Fused} < [{i}]={expanded[i].Fused}");
    }

    /// <summary>A2 — a link target already in the fused pool is NOT duplicated.</summary>
    [Fact]
    public void ExpandNeighbors_LinkTargetAlreadyInPool_NotDuplicated()
    {
        var parent = Entry("parent", links: ("repo-a", "alsoInPool"));
        var alsoInPool = Entry("alsoInPool");
        var fused = new List<FusedCandidate>
        {
            Candidate(parent, fused: 0.9),
            Candidate(alsoInPool, fused: 0.4),
        };

        var resolved = new Dictionary<string, MemoryEntry> { ["alsoInPool"] = alsoInPool };
        var expanded = RecallScoring.ExpandNeighbors(
            fused, id => resolved.GetValueOrDefault(id), RecallWeights.Default, Now);

        Assert.Equal(2, expanded.Count);
        Assert.Single(expanded, c => c.Entry.Id == "alsoInPool");
        // The pre-existing pool entry keeps its own fused score (not the damped-inherited one).
        Assert.Equal(0.4, expanded.Single(c => c.Entry.Id == "alsoInPool").Fused, precision: 9);
    }

    /// <summary>A2b — two parents linking the SAME off-pool neighbor add it exactly once.</summary>
    [Fact]
    public void ExpandNeighbors_TwoParentsSameNeighbor_AddedOnce()
    {
        var p1 = Entry("p1", links: ("repo-a", "shared"));
        var p2 = Entry("p2", links: ("repo-a", "shared"));
        var shared = Entry("shared");
        var fused = new List<FusedCandidate> { Candidate(p1, 0.9), Candidate(p2, 0.7) };

        var resolved = new Dictionary<string, MemoryEntry> { ["shared"] = shared };
        var expanded = RecallScoring.ExpandNeighbors(
            fused, id => resolved.GetValueOrDefault(id), RecallWeights.Default, Now);

        Assert.Single(expanded, c => c.Entry.Id == "shared");
        // Inherits from the FIRST (strongest) parent it was reached through (0.9), not p2.
        var recency = FadeMemCurve.Recency(shared.CreatedAt, shared.LastAccessedAt, Now, shared.Type);
        Assert.Equal(0.9 * 0.5 + recency, expanded.Single(c => c.Entry.Id == "shared").Fused, precision: 9);
    }

    /// <summary>A3 — with more than 5 distinct linkable neighbors, at most 5 are added.</summary>
    [Fact]
    public void ExpandNeighbors_MoreThanCap_AddsAtMostFive()
    {
        var links = Enumerable.Range(0, 8).Select(i => ("repo-a", $"n{i}")).ToArray();
        var parent = Entry("parent", links: links);
        var fused = new List<FusedCandidate> { Candidate(parent, 0.9) };

        var resolved = Enumerable.Range(0, 8).ToDictionary(i => $"n{i}", i => Entry($"n{i}"));
        var expanded = RecallScoring.ExpandNeighbors(
            fused, id => resolved.GetValueOrDefault(id), RecallWeights.Default, Now);

        var added = expanded.Count(c => c.Entry.Id.StartsWith('n'));
        Assert.Equal(5, added);
        Assert.Equal(6, expanded.Count); // parent + 5
    }

    /// <summary>
    /// A4 — one hop only: a neighbor's OWN links are never expanded, so a neighbor-of-neighbor
    /// never appears even though it is resolvable.
    /// </summary>
    [Fact]
    public void ExpandNeighbors_OneHopOnly_NeighborOfNeighborNeverAppears()
    {
        var parent = Entry("parent", links: ("repo-a", "neighbor"));
        var neighbor = Entry("neighbor", links: ("repo-a", "grandchild"));
        var grandchild = Entry("grandchild");
        var fused = new List<FusedCandidate> { Candidate(parent, 0.9) };

        // resolve can reach the grandchild too, but expansion must not follow the neighbor's link.
        var resolved = new Dictionary<string, MemoryEntry>
        {
            ["neighbor"] = neighbor,
            ["grandchild"] = grandchild,
        };
        var expanded = RecallScoring.ExpandNeighbors(
            fused, id => resolved.GetValueOrDefault(id), RecallWeights.Default, Now);

        Assert.Contains(expanded, c => c.Entry.Id == "neighbor");
        Assert.DoesNotContain(expanded, c => c.Entry.Id == "grandchild");
    }

    /// <summary>
    /// A5 — a link whose target the resolver can't produce (null) is silently skipped; the pool is
    /// returned re-sorted and otherwise untouched.
    /// </summary>
    [Fact]
    public void ExpandNeighbors_UnresolvableTarget_SkippedNoThrow()
    {
        var parent = Entry("parent", links: ("repo-a", "missing"));
        var fused = new List<FusedCandidate> { Candidate(parent, 0.9) };

        var expanded = RecallScoring.ExpandNeighbors(
            fused, _ => null, RecallWeights.Default, Now);

        Assert.Single(expanded);
        Assert.Equal("parent", expanded[0].Entry.Id);
    }

    /// <summary>
    /// A6 — only the top <c>parentTopK</c> candidates spread. A link hanging off a candidate ranked
    /// below the cut is not followed.
    /// </summary>
    [Fact]
    public void ExpandNeighbors_OnlyTopKParentsSpread()
    {
        var rich = Entry("rich", links: ("repo-a", "fromRich"));
        var poor = Entry("poor", links: ("repo-a", "fromPoor"));
        var fused = new List<FusedCandidate> { Candidate(rich, 0.9), Candidate(poor, 0.1) };

        var resolved = new Dictionary<string, MemoryEntry>
        {
            ["fromRich"] = Entry("fromRich"),
            ["fromPoor"] = Entry("fromPoor"),
        };
        // parentTopK = 1 ⇒ only the strongest parent (rich) spreads.
        var expanded = RecallScoring.ExpandNeighbors(
            fused, id => resolved.GetValueOrDefault(id), RecallWeights.Default, Now, parentTopK: 1);

        Assert.Contains(expanded, c => c.Entry.Id == "fromRich");
        Assert.DoesNotContain(expanded, c => c.Entry.Id == "fromPoor");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Section B — behavioral through MemoryService.RecallAsync
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// B1 — a gold memory in NEITHER arm but linked from a top arm-hit SURFACES when ExpandGraph is on
    /// (the default) and is ABSENT when ExpandGraph=false.
    /// </summary>
    [Fact]
    public async Task RecallAsync_LinkedGold_SurfacesWithExpansion_AbsentWithout()
    {
        GraphStore Build()
        {
            var store = new GraphStore();
            var parent = Entry("parent", links: ("repo-a", "gold"));
            var gold = Entry("gold"); // in NO arm
            store.SetArm(SearchArm.Lexical, ("parent", 7.0));
            store.SetArm(SearchArm.Vector, ("parent", 7.0));
            store.Seed(parent, gold);
            return store;
        }

        var withExpansion = await new MemoryService(Build()).RecallAsync(
            "repo-a", new RecallOptions("q")); // ExpandGraph default true
        var withoutExpansion = await new MemoryService(Build()).RecallAsync(
            "repo-a", new RecallOptions("q") { ExpandGraph = false });

        Assert.Contains(withExpansion, r => r.Id == "gold");
        Assert.DoesNotContain(withoutExpansion, r => r.Id == "gold");
    }

    /// <summary>
    /// B2 — the SCOPE-LEAK fix. A parent links a neighbor whose declared TargetRepoId LOOKS in-scope
    /// ("repo-a") but whose LOADED entry actually belongs to an out-of-scope repo ("repo-b"). The
    /// authoritative re-check on the loaded entry's real RepoId must reject it.
    /// </summary>
    [Fact]
    public async Task RecallAsync_NeighborRealRepoOutOfScope_NotAdmitted_DespiteInScopeLinkRepo()
    {
        var store = new GraphStore();
        // Link DECLARES repo-a (in scope) ...
        var parent = Entry("parent", links: ("repo-a", "leak"));
        // ... but the resolved entry's REAL RepoId is repo-b (NOT searched by this recall).
        var leak = Entry("leak", repoId: "repo-b");
        store.SetArm(SearchArm.Lexical, ("parent", 7.0));
        store.SetArm(SearchArm.Vector, ("parent", 7.0));
        store.Seed(parent, leak);

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Contains(results, r => r.Id == "parent");
        Assert.DoesNotContain(results, r => r.Id == "leak");
    }

    /// <summary>
    /// B2b — the cheap pre-filter also rejects a link whose DECLARED TargetRepoId is out of scope, even
    /// when the loaded entry would have been in scope. (Guards the pre-filter branch; the link names
    /// repo-b so the neighbor is never even loaded.)
    /// </summary>
    [Fact]
    public async Task RecallAsync_NeighborDeclaredRepoOutOfScope_NotAdmitted()
    {
        var store = new GraphStore();
        var parent = Entry("parent", links: ("repo-b", "neighbor")); // declared out of scope
        var neighbor = Entry("neighbor", repoId: "repo-a");          // would be in scope if reached
        store.SetArm(SearchArm.Lexical, ("parent", 7.0));
        store.SetArm(SearchArm.Vector, ("parent", 7.0));
        store.Seed(parent, neighbor);

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.DoesNotContain(results, r => r.Id == "neighbor");
    }

    /// <summary>
    /// B3 — best-effort: a neighbor load that THROWS doesn't fail recall — the direct arm hits still return.
    /// </summary>
    [Fact]
    public async Task RecallAsync_NeighborLoadThrows_DirectHitsStillReturn()
    {
        var store = new GraphStore { ThrowOnGet = "boom" };
        var parent = Entry("parent", links: ("repo-a", "boom"));
        store.SetArm(SearchArm.Lexical, ("parent", 7.0));
        store.SetArm(SearchArm.Vector, ("parent", 7.0));
        store.Seed(parent, Entry("boom"));

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Contains(results, r => r.Id == "parent");
        Assert.DoesNotContain(results, r => r.Id == "boom");
    }

    /// <summary>
    /// B3b — a neighbor that resolves to a NON-LATEST (superseded) entry is rejected; recall still
    /// returns the direct hit.
    /// </summary>
    [Fact]
    public async Task RecallAsync_NeighborNotLatest_NotAdmitted()
    {
        var store = new GraphStore();
        var parent = Entry("parent", links: ("repo-a", "stale"));
        var stale = Entry("stale", isLatest: false);
        store.SetArm(SearchArm.Lexical, ("parent", 7.0));
        store.SetArm(SearchArm.Vector, ("parent", 7.0));
        store.Seed(parent, stale);

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Contains(results, r => r.Id == "parent");
        Assert.DoesNotContain(results, r => r.Id == "stale");
    }

    /// <summary>
    /// B3c — a neighbor whose GetAsync returns null (missing) is skipped; recall still returns the hit.
    /// </summary>
    [Fact]
    public async Task RecallAsync_NeighborMissing_NotAdmitted()
    {
        var store = new GraphStore();
        var parent = Entry("parent", links: ("repo-a", "ghost")); // ghost never seeded
        store.SetArm(SearchArm.Lexical, ("parent", 7.0));
        store.SetArm(SearchArm.Vector, ("parent", 7.0));
        store.Seed(parent);

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Contains(results, r => r.Id == "parent");
        Assert.DoesNotContain(results, r => r.Id == "ghost");
    }
}

/// <summary>
/// Scripted-arm store for the behavioral expansion tests. Like <c>InMemoryScoredStore</c> but with two
/// extra knobs the wrapper needs exercised: <see cref="GetAsync"/> returns seeded entries (so neighbor
/// loads resolve) filtered by no scope (the SERVICE enforces scope, which is exactly what B2 tests), and
/// <see cref="ThrowOnGet"/> makes a specific neighbor load throw to prove best-effort recovery.
/// </summary>
internal sealed class GraphStore : IEidetStore
{
    private readonly Dictionary<SearchArm, List<(string Id, double Score)>> _arms = new()
    {
        [SearchArm.Lexical] = [],
        [SearchArm.Vector] = [],
    };
    private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Id whose GetAsync throws — models a transient backend failure during neighbor load.</summary>
    public string? ThrowOnGet { get; init; }

    public void SetArm(SearchArm arm, params (string Id, double Score)[] hits) => _arms[arm] = hits.ToList();

    public void Seed(params MemoryEntry[] entries)
    {
        foreach (var e in entries) _entries[e.Id] = e;
    }

    public Task<IReadOnlyList<ScoredHit>> SearchScoredAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        // Arm hits are scoped by the repos this recall searches — exactly like the real backend.
        var hits = _arms[arm]
            .Where(h => _entries.ContainsKey(h.Id) && repoIds.Contains(_entries[h.Id].RepoId, StringComparer.OrdinalIgnoreCase))
            .Select(h => new ScoredHit(_entries[h.Id], h.Score))
            .ToList();
        return Task.FromResult<IReadOnlyList<ScoredHit>>(hits);
    }

    public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        if (ThrowOnGet is not null && string.Equals(id, ThrowOnGet, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("simulated backend failure");
        return Task.FromResult(_entries.GetValueOrDefault(id));
    }

    public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Id] = entry;
        return Task.FromResult(entry.Id);
    }

    public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    // ── Unused interface surface ──
    public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());
    public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());
    public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) =>
        Task.FromResult<MemoryEntry?>(null);
    public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<MemoryType, int>());
    public Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());
    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => Task.FromResult<DatabaseInfo?>(null);
    public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default) =>
        Task.FromResult(_entries.Values.Select(e => e.RepoId).Distinct().ToList());
    public Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());
    public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default) => Task.FromResult(layer.Id);
    public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default) => Task.FromResult(new List<MemoryLayer>());
    public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult<MemoryLayer?>(null);
    public Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
    public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(_entries.Remove(id));
}
