using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Tests for CUE-ANCHOR expansion — reaching related memories through shared entities rather than
/// through links somebody authored. Three surfaces:
///
/// Section A — the pure <see cref="RecallScoring.ExpandEntities"/> math: a cue match inherits
/// <c>bestParentFused·cueDecay + own(ucb+recency)</c>, is attributed to the STRONGEST parent it shares
/// a cue with, is capped best-score-first, and never duplicates a memory already in the pool.
///
/// Section B — the <see cref="MemoryService"/> I/O wrapper through RecallAsync: a gold memory in no arm
/// and linked from nothing surfaces on shared entities alone; the flag turns it off; and the admission
/// guards (forgotten, superseded, out-of-scope) hold — a forgotten memory reaching a live recall through
/// this path is the same leak the auditor's EntityNeighbor probe exists to catch.
///
/// Section C — the two expansions are independent: cue expansion works with ExpandGraph off, and the
/// weaker cue decay means a memory reachable BOTH ways keeps its stronger link-inherited score.
/// </summary>
public class EntityExpansionTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryEntry Entry(
        string id, string repoId = "repo-a", bool isLatest = true,
        DateTime? validUntil = null, string[]? entities = null,
        params (string repo, string target)[] links)
    {
        var e = new MemoryEntry
        {
            Id = id,
            RepoId = repoId,
            Type = MemoryType.Insight,
            Content = id,
            CreatedAt = Now,
            Validity = new Validity { ValidFrom = Now, ValidUntil = validUntil },
            IsLatest = isLatest,
            Importance = 0.5f,
            Entities = (entities ?? []).ToList(),
        };
        foreach (var (repo, target) in links)
            e.Links.Add(new MemoryLink { TargetRepoId = repo, TargetMemoryId = target, Relation = "supports" });
        return e;
    }

    private static FusedCandidate Candidate(MemoryEntry entry, double fused) =>
        new(entry, Lex: 0, Vec: 0, Abs: 0, Recency: 0, Ucb: 0, Fused: fused);

    // ════════════════════════════════════════════════════════════════════════
    // Section A — pure ExpandEntities math
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A1 — a cue match inherits parentFused·0.35 plus its own recency+UCB, enters with all three arm
    /// components at 0 (it was in no arm), and the output is re-sorted descending.
    /// </summary>
    [Fact]
    public void ExpandEntities_SharedEntity_InheritsDampedParentScore()
    {
        var parent = Entry("parent", entities: ["RavenDB"]);
        var match = Entry("match", entities: ["RavenDB"]);
        var fused = new List<FusedCandidate> { Candidate(parent, fused: 0.8) };

        var expanded = RecallScoring.ExpandEntities(fused, [match], RecallWeights.Default, Now);

        var m = expanded.Single(c => c.Entry.Id == "match");
        Assert.Equal(0.0, m.Lex);
        Assert.Equal(0.0, m.Vec);
        Assert.Equal(0.0, m.Abs);

        var ucb = 0.0; // TotalN=0 ⇒ ln(1)=0 ⇒ ucb term 0
        var recency = FadeMemCurve.Recency(match.CreatedAt, match.LastAccessedAt, Now, match.Type);
        Assert.Equal(0.8 * 0.35 + ucb + recency, m.Fused, precision: 9);

        for (var i = 1; i < expanded.Count; i++)
            Assert.True(expanded[i - 1].Fused >= expanded[i].Fused,
                $"expanded must be sorted descending: [{i - 1}]={expanded[i - 1].Fused} < [{i}]={expanded[i].Fused}");
    }

    /// <summary>A2 — cue overlap is many-to-many, so a match sharing cues with several parents
    /// inherits from the STRONGEST of them, not the first or the last seen.</summary>
    [Fact]
    public void ExpandEntities_MatchesSeveralParents_InheritsFromStrongest()
    {
        var strong = Entry("strong", entities: ["RavenDB"]);
        var weak = Entry("weak", entities: ["RavenDB"]);
        var match = Entry("match", entities: ["RavenDB"]);
        // Weak first in the list, to prove the rule is "strongest", not "first".
        var fused = new List<FusedCandidate> { Candidate(weak, 0.2), Candidate(strong, 0.9) };

        var expanded = RecallScoring.ExpandEntities(fused, [match], RecallWeights.Default, Now);

        var recency = FadeMemCurve.Recency(match.CreatedAt, match.LastAccessedAt, Now, match.Type);
        Assert.Equal(0.9 * 0.35 + recency, expanded.Single(c => c.Entry.Id == "match").Fused, precision: 9);
    }

    /// <summary>A3 — a candidate sharing NO entity with any parent is not admitted, even though the
    /// store handed it back (the store matches over the whole cue set; attribution is per-parent).</summary>
    [Fact]
    public void ExpandEntities_NoSharedEntity_NotAdmitted()
    {
        var parent = Entry("parent", entities: ["RavenDB"]);
        var unrelated = Entry("unrelated", entities: ["Postgres"]);
        var fused = new List<FusedCandidate> { Candidate(parent, 0.8) };

        var expanded = RecallScoring.ExpandEntities(fused, [unrelated], RecallWeights.Default, Now);

        Assert.Single(expanded);
        Assert.Equal("parent", expanded[0].Entry.Id);
    }

    /// <summary>A3b — entity matching is case-insensitive; enrichment casing must not decide reachability.</summary>
    [Fact]
    public void ExpandEntities_EntityCasingDiffers_StillMatches()
    {
        var parent = Entry("parent", entities: ["RavenDB"]);
        var match = Entry("match", entities: ["ravendb"]);
        var fused = new List<FusedCandidate> { Candidate(parent, 0.8) };

        var expanded = RecallScoring.ExpandEntities(fused, [match], RecallWeights.Default, Now);

        Assert.Contains(expanded, c => c.Entry.Id == "match");
    }

    /// <summary>A4 — a match already in the fused pool is not duplicated and keeps its own score.</summary>
    [Fact]
    public void ExpandEntities_MatchAlreadyInPool_NotDuplicated()
    {
        var parent = Entry("parent", entities: ["RavenDB"]);
        var alsoInPool = Entry("alsoInPool", entities: ["RavenDB"]);
        var fused = new List<FusedCandidate> { Candidate(parent, 0.9), Candidate(alsoInPool, 0.4) };

        var expanded = RecallScoring.ExpandEntities(fused, [alsoInPool], RecallWeights.Default, Now);

        Assert.Equal(2, expanded.Count);
        Assert.Equal(0.4, expanded.Single(c => c.Entry.Id == "alsoInPool").Fused, precision: 9);
    }

    /// <summary>A5 — an entry with no entities can never be pulled in (nothing to match on).</summary>
    [Fact]
    public void ExpandEntities_MatchWithNoEntities_NotAdmitted()
    {
        var parent = Entry("parent", entities: ["RavenDB"]);
        var unenriched = Entry("unenriched"); // Entities empty — the pre-enrichment state
        var fused = new List<FusedCandidate> { Candidate(parent, 0.8) };

        var expanded = RecallScoring.ExpandEntities(fused, [unenriched], RecallWeights.Default, Now);

        Assert.Single(expanded);
    }

    /// <summary>A6 — the cap keeps the BEST-inherited matches, not the first-seen ones.</summary>
    [Fact]
    public void ExpandEntities_CapKeepsHighestInherited()
    {
        var strong = Entry("strong", entities: ["A"]);
        var weak = Entry("weak", entities: ["B"]);
        var fromStrong = Entry("fromStrong", entities: ["A"]);
        var fromWeak = Entry("fromWeak", entities: ["B"]);
        var fused = new List<FusedCandidate> { Candidate(strong, 0.9), Candidate(weak, 0.1) };

        // fromWeak is offered FIRST; the cap of 1 must still keep fromStrong.
        var expanded = RecallScoring.ExpandEntities(
            fused, [fromWeak, fromStrong], RecallWeights.Default, Now, maxNeighbors: 1);

        Assert.Contains(expanded, c => c.Entry.Id == "fromStrong");
        Assert.DoesNotContain(expanded, c => c.Entry.Id == "fromWeak");
    }

    /// <summary>A7 — only the top <c>parentTopK</c> candidates spread their cues.</summary>
    [Fact]
    public void ExpandEntities_OnlyTopKParentsSpread()
    {
        var rich = Entry("rich", entities: ["A"]);
        var poor = Entry("poor", entities: ["B"]);
        var fromPoor = Entry("fromPoor", entities: ["B"]);
        var fused = new List<FusedCandidate> { Candidate(rich, 0.9), Candidate(poor, 0.1) };

        var expanded = RecallScoring.ExpandEntities(
            fused, [fromPoor], RecallWeights.Default, Now, parentTopK: 1);

        Assert.DoesNotContain(expanded, c => c.Entry.Id == "fromPoor");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Section B — behavioral through MemoryService.RecallAsync
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// B1 — the headline: a gold memory in NEITHER arm and linked from NOTHING surfaces purely because
    /// it shares an entity with a top hit, and is absent when ExpandEntities=false.
    /// </summary>
    [Fact]
    public async Task RecallAsync_CueMatchedGold_SurfacesWithExpansion_AbsentWithout()
    {
        CueStore Build()
        {
            var store = new CueStore();
            store.SetArm(SearchArm.Lexical, ("hit", 7.0));
            store.SetArm(SearchArm.Vector, ("hit", 7.0));
            store.Seed(Entry("hit", entities: ["RavenDB"]), Entry("gold", entities: ["RavenDB"]));
            return store;
        }

        var with = await new MemoryService(Build()).RecallAsync("repo-a", new RecallOptions("q"));
        var without = await new MemoryService(Build()).RecallAsync(
            "repo-a", new RecallOptions("q") { ExpandEntities = false });

        Assert.Contains(with, r => r.Id == "gold");
        Assert.DoesNotContain(without, r => r.Id == "gold");
    }

    /// <summary>
    /// B2 — THE LEAK GUARD. Forget stamps ValidUntil but leaves IsLatest true, so a store that hands
    /// back a forgotten cue match (as a lax backend would) must still be rejected by the service.
    /// This is the invariant <see cref="Core.Integrity.IntegrityCheck.EntityNeighbor"/> probes live.
    /// </summary>
    [Fact]
    public async Task RecallAsync_ForgottenCueMatch_NotAdmitted()
    {
        var store = new CueStore { IgnoreValidityFilter = true };
        store.SetArm(SearchArm.Lexical, ("hit", 7.0));
        store.SetArm(SearchArm.Vector, ("hit", 7.0));
        store.Seed(
            Entry("hit", entities: ["RavenDB"]),
            Entry("forgotten", entities: ["RavenDB"], validUntil: Now.AddDays(-1)));

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Contains(results, r => r.Id == "hit");
        Assert.DoesNotContain(results, r => r.Id == "forgotten");
    }

    /// <summary>B2b — a superseded (non-latest) cue match is likewise rejected.</summary>
    [Fact]
    public async Task RecallAsync_SupersededCueMatch_NotAdmitted()
    {
        var store = new CueStore { IgnoreValidityFilter = true };
        store.SetArm(SearchArm.Lexical, ("hit", 7.0));
        store.SetArm(SearchArm.Vector, ("hit", 7.0));
        store.Seed(
            Entry("hit", entities: ["RavenDB"]),
            Entry("superseded", entities: ["RavenDB"], isLatest: false));

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.DoesNotContain(results, r => r.Id == "superseded");
    }

    /// <summary>
    /// B3 — scope is re-checked on the entry's REAL RepoId, exactly as link expansion does: a cue match
    /// from a repo this recall isn't searching is never admitted.
    /// </summary>
    [Fact]
    public async Task RecallAsync_CueMatchOutOfScope_NotAdmitted()
    {
        var store = new CueStore { IgnoreRepoFilter = true };
        store.SetArm(SearchArm.Lexical, ("hit", 7.0));
        store.SetArm(SearchArm.Vector, ("hit", 7.0));
        store.Seed(
            Entry("hit", entities: ["RavenDB"]),
            Entry("leak", repoId: "repo-b", entities: ["RavenDB"]));

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Contains(results, r => r.Id == "hit");
        Assert.DoesNotContain(results, r => r.Id == "leak");
    }

    /// <summary>B4 — best-effort: a cue lookup that THROWS doesn't fail recall.</summary>
    [Fact]
    public async Task RecallAsync_CueLookupThrows_DirectHitsStillReturn()
    {
        var store = new CueStore { ThrowOnCueLookup = true };
        store.SetArm(SearchArm.Lexical, ("hit", 7.0));
        store.SetArm(SearchArm.Vector, ("hit", 7.0));
        store.Seed(Entry("hit", entities: ["RavenDB"]));

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Contains(results, r => r.Id == "hit");
    }

    /// <summary>B5 — no entities on the hits means no cues, so the store is never even asked.</summary>
    [Fact]
    public async Task RecallAsync_HitsWithoutEntities_SkipsCueLookupEntirely()
    {
        var store = new CueStore();
        store.SetArm(SearchArm.Lexical, ("hit", 7.0));
        store.SetArm(SearchArm.Vector, ("hit", 7.0));
        store.Seed(Entry("hit"), Entry("gold", entities: ["RavenDB"]));

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Equal(0, store.CueLookups);
        Assert.DoesNotContain(results, r => r.Id == "gold");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Section C — the two expansion paths are independent
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>C1 — cue expansion is not a rider on graph expansion: it still runs with ExpandGraph off.</summary>
    [Fact]
    public async Task RecallAsync_CueExpansionIndependentOfGraphExpansion()
    {
        var store = new CueStore();
        store.SetArm(SearchArm.Lexical, ("hit", 7.0));
        store.SetArm(SearchArm.Vector, ("hit", 7.0));
        store.Seed(Entry("hit", entities: ["RavenDB"]), Entry("gold", entities: ["RavenDB"]));

        var results = await new MemoryService(store).RecallAsync(
            "repo-a", new RecallOptions("q") { ExpandGraph = false });

        Assert.Contains(results, r => r.Id == "gold");
    }

    /// <summary>
    /// C2 — a memory reachable BOTH ways is admitted once, by the link, at the stronger decay: link
    /// expansion runs first and cue expansion skips anything already in the pool. An authored link
    /// outranking a shared entity string is the whole reason the two decays differ.
    /// </summary>
    [Fact]
    public async Task RecallAsync_ReachableBothWays_KeepsStrongerLinkScore()
    {
        var store = new CueStore();
        store.SetArm(SearchArm.Lexical, ("hit", 7.0));
        store.SetArm(SearchArm.Vector, ("hit", 7.0));
        store.Seed(
            Entry("hit", entities: ["RavenDB"], links: ("repo-a", "both")),
            Entry("both", entities: ["RavenDB"]));

        var viaBoth = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        var linkOnlyStore = new CueStore();
        linkOnlyStore.SetArm(SearchArm.Lexical, ("hit", 7.0));
        linkOnlyStore.SetArm(SearchArm.Vector, ("hit", 7.0));
        linkOnlyStore.Seed(
            Entry("hit", links: ("repo-a", "both")),
            Entry("both"));
        var viaLink = await new MemoryService(linkOnlyStore).RecallAsync("repo-a", new RecallOptions("q"));

        // Admitted exactly once, and at the link-expansion score — cue expansion never re-scored it down.
        Assert.Single(viaBoth, r => r.Id == "both");
        Assert.Equal(
            viaLink.Single(r => r.Id == "both").Score,
            viaBoth.Single(r => r.Id == "both").Score,
            precision: 6);
    }
}

/// <summary>
/// Scripted-arm store for the cue-expansion tests. <see cref="FindByEntitiesAsync"/> is the surface
/// under test, so it applies the store-side contract (latest + valid, exclusions, cap) by default —
/// and the three knobs let a test relax one part of it to prove the SERVICE re-checks independently.
/// A store that forgets to filter must not be able to leak through recall.
/// </summary>
internal sealed class CueStore : IEidetStore
{
    private readonly Dictionary<SearchArm, List<(string Id, double Score)>> _arms = new();
    private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hand back forgotten/superseded matches — models a backend that skips the validity filter.</summary>
    public bool IgnoreValidityFilter { get; init; }

    /// <summary>Hand back matches from any repo — models a backend that skips the scope filter.</summary>
    public bool IgnoreRepoFilter { get; init; }

    /// <summary>Make the cue lookup throw, to prove expansion is best-effort.</summary>
    public bool ThrowOnCueLookup { get; init; }

    /// <summary>How many times the cue lookup was called — lets a test assert it was skipped.</summary>
    public int CueLookups { get; private set; }

    public void SetArm(SearchArm arm, params (string Id, double Score)[] hits) => _arms[arm] = hits.ToList();

    public void Seed(params MemoryEntry[] entries)
    {
        foreach (var e in entries) _entries[e.Id] = e;
    }

    public Task<IReadOnlyList<ScoredHit>> SearchScoredAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        var hits = (_arms.GetValueOrDefault(arm) ?? [])
            .Where(h => _entries.ContainsKey(h.Id) && repoIds.Contains(_entries[h.Id].RepoId, StringComparer.OrdinalIgnoreCase))
            .Select(h => new ScoredHit(_entries[h.Id], h.Score))
            .ToList();
        return Task.FromResult<IReadOnlyList<ScoredHit>>(hits);
    }

    public Task<IReadOnlyList<MemoryEntry>> FindByEntitiesAsync(
        IReadOnlyList<string> repoIds, IReadOnlyCollection<string> entities,
        IReadOnlyCollection<string> excludeIds, int max, CancellationToken ct = default)
    {
        CueLookups++;
        if (ThrowOnCueLookup) throw new InvalidOperationException("simulated backend failure");

        var cues = new HashSet<string>(entities, StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(excludeIds, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<MemoryEntry> matches = _entries.Values
            .Where(e => !excluded.Contains(e.Id))
            .Where(e => e.Entities.Any(cues.Contains))
            .Where(e => IgnoreRepoFilter || repoIds.Contains(e.RepoId, StringComparer.OrdinalIgnoreCase))
            .Where(e => IgnoreValidityFilter || (e.IsLatest && e.Validity.ValidUntil is null))
            .Take(max)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_entries.GetValueOrDefault(id));

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
