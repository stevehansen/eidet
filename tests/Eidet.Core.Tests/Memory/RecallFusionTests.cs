using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Tests for the Recall pipeline v2 contract (issue #33, spine items 1-5): min-max-normalized
/// hybrid fusion (Alpha=0.5 lexical/vector blend) + UCB exploration (Kappa=0.3) + dual-clock
/// FadeMem recency, replacing the old flat 1.0/0.9 constant scoring.
///
/// The headline guarantee: a "gold" candidate with strong combined relevance survives truncation
/// and ranks top-k after fusion, where flat scoring would have lost it.
///
/// Section A — pure fusion math (RecallScoring.Fuse / FuseAndScore, no Raven, the CI oracle guard).
/// Section B — the default SearchScoredAsync rank-decay shim (≈19 fakes work untouched).
/// Section C — MemoryService.ExplainRecallAsync diagnostics.
/// Section D — end-to-end fused ranking through MemoryService.RecallAsync.
/// </summary>
public class RecallFusionTests
{
    // ─── Fixed "now" so the recency clock is deterministic across the suite ───
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Builds a MemoryEntry with knobs the fusion math reads. Defaults are "neutral":
    /// just-created (recency ≈ 1), no feedback. Id doubles as the search key.
    /// </summary>
    private static MemoryEntry Entry(
        string id,
        MemoryType type = MemoryType.Insight,
        DateTime? createdAt = null,
        DateTime? lastAccessedAt = null,
        int echo = 0,
        int fizzle = 0,
        string repoId = "repo-a") => new()
    {
        Id = id,
        RepoId = repoId,
        Type = type,
        Content = id,
        CreatedAt = createdAt ?? Now,
        LastAccessedAt = lastAccessedAt,
        EchoCount = echo,
        FizzleCount = fizzle,
        IsLatest = true,
        Validity = new Validity { ValidFrom = createdAt ?? Now },
        Importance = 0.5f,
    };

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

    // ════════════════════════════════════════════════════════════════════════
    // Section A — pure fusion math (the CI oracle guard)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A1 — THE test the issue requires. Gold has strong COMBINED relevance: it is decent (not top)
    /// in the lexical arm AND the strongest in the vector arm. Each distractor is strong in exactly
    /// ONE arm. Under the old flat per-arm constant scoring (1.0 lex / 0.9 vec) gold was just another
    /// lexical 1.0 — indistinguishable from the lexical-only distractors and a truncation casualty.
    /// Min-max fusion lifts gold above every single-arm distractor; it survives a small-limit budget
    /// and lands top-k.
    /// </summary>
    [Fact]
    public void Fuse_GoldSurvivesTruncation_RanksTopK_whereFlatScoringWouldLoseIt()
    {
        var gold = Entry("gold");
        var lexOnly1 = Entry("lexOnly1");
        var lexOnly2 = Entry("lexOnly2");
        var vecOnly1 = Entry("vecOnly1");

        // Lexical arm: gold is mid-pack (normLex = 0.5), two distractors bracket it.
        var lex = new List<ScoredHit>
        {
            new(lexOnly1, 10.0), // normLex 1.0 — but absent from vec ⇒ normVec 0
            new(gold, 7.5),      // normLex 0.5
            new(lexOnly2, 5.0),  // normLex 0.0
        };

        // Vector arm: gold is the strongest; one vec-only distractor is mid.
        var vec = new List<ScoredHit>
        {
            new(gold, 10.0),     // normVec 1.0
            new(vecOnly1, 5.0),  // normVec 0.0 (min of arm) — absent from lex ⇒ normLex 0
        };

        var fused = RecallScoring.Fuse(lex, vec, RecallWeights.Default, Now);

        // Gold: 0.5·0.5 + 0.5·1.0 + recency = 0.75 + recency — beats every single-arm distractor:
        //   lexOnly1: 0.5·1.0 + 0 + recency = 0.5 + recency
        //   any vec-only / lex-only peaks at 0.5 + recency.
        Assert.Equal("gold", fused[0].Entry.Id);

        // And it survives a downstream small-limit budget pass (limit 2 of 4 candidates).
        var results = RecallScoring.FuseAndScore(lex, vec, RecallWeights.Default, Now);
        var budgeted = RecallScoring.ApplyTypeBudgets(results, limit: 2);
        Assert.Contains(budgeted, r => r.Id == "gold");
        Assert.Equal("gold", budgeted[0].Id);
    }

    /// <summary>
    /// A2a — robust degrade: an empty lexical arm with a populated vector arm still returns results,
    /// ordered by the vector arm.
    /// </summary>
    [Fact]
    public void Fuse_EmptyLexArm_ReturnsResultsOrderedByVecArm()
    {
        var a = Entry("a");
        var b = Entry("b");
        var c = Entry("c");

        var lex = new List<ScoredHit>();
        var vec = new List<ScoredHit>
        {
            new(a, 1.0),
            new(b, 5.0),
            new(c, 9.0),
        };

        var fused = RecallScoring.Fuse(lex, vec, RecallWeights.Default, Now);

        Assert.Equal(3, fused.Count);
        // All recency/UCB equal ⇒ ordering is driven entirely by the normalized vec arm.
        Assert.Equal(new[] { "c", "b", "a" }, fused.Select(f => f.Entry.Id).ToArray());
        // Every lex component is 0 (empty arm → 0 for all).
        Assert.All(fused, f => Assert.Equal(0.0, f.Lex));
    }

    /// <summary>
    /// A2b — all-equal / single-candidate arm (max==min) must not divide by zero: that arm
    /// contributes a finite normalized 1.0, and NO component is NaN/Infinity.
    /// </summary>
    [Fact]
    public void Fuse_AllEqualArm_NoNaNorInfinity_NormalizesToOne()
    {
        var a = Entry("a");
        var b = Entry("b");

        // Lexical arm: both equal (range == 0) → each normalizes to 1.0.
        var lex = new List<ScoredHit> { new(a, 7.0), new(b, 7.0) };
        // Vector arm: single candidate (max==min) → normalizes to 1.0.
        var vec = new List<ScoredHit> { new(a, 3.0) };

        var fused = RecallScoring.Fuse(lex, vec, RecallWeights.Default, Now);

        Assert.All(fused, f =>
        {
            Assert.True(IsFinite(f.Lex), $"Lex not finite for {f.Entry.Id}");
            Assert.True(IsFinite(f.Vec), $"Vec not finite for {f.Entry.Id}");
            Assert.True(IsFinite(f.Recency), $"Recency not finite for {f.Entry.Id}");
            Assert.True(IsFinite(f.Ucb), $"Ucb not finite for {f.Entry.Id}");
            Assert.True(IsFinite(f.Fused), $"Fused not finite for {f.Entry.Id}");
        });

        // Both lexical hits normalize to 1.0; the single vector hit normalizes to 1.0.
        Assert.Equal(1.0, fused.Single(f => f.Entry.Id == "a").Lex);
        Assert.Equal(1.0, fused.Single(f => f.Entry.Id == "b").Lex);
        Assert.Equal(1.0, fused.Single(f => f.Entry.Id == "a").Vec);
    }

    /// <summary>
    /// A3 — outer-join over lex∪vec: a lex-only entry and a vec-only entry both appear; the
    /// lex-only entry's Vec component is 0 and the vec-only entry's Lex component is 0.
    /// </summary>
    [Fact]
    public void Fuse_OuterJoin_LexOnlyAndVecOnlyBothAppear_WithZeroOtherArm()
    {
        var lexOnly = Entry("lexOnly");
        var vecOnly = Entry("vecOnly");

        var lex = new List<ScoredHit> { new(lexOnly, 4.0) };
        var vec = new List<ScoredHit> { new(vecOnly, 4.0) };

        var fused = RecallScoring.Fuse(lex, vec, RecallWeights.Default, Now);

        Assert.Equal(2, fused.Count);
        var l = fused.Single(f => f.Entry.Id == "lexOnly");
        var v = fused.Single(f => f.Entry.Id == "vecOnly");

        Assert.Equal(0.0, l.Vec);          // present only in lex arm
        Assert.Equal(0.0, v.Lex);          // present only in vec arm
        Assert.True(l.Lex > 0.0);          // single-candidate arm normalizes to 1.0
        Assert.True(v.Vec > 0.0);
    }

    /// <summary>
    /// A4 — UCB exploration. With TotalN>0, between two entries identical in every arm/recency
    /// dimension but differing only in feedback counts, the one with FEWER (Echo+Fizzle) earns a
    /// higher UCB term and ranks higher.
    /// </summary>
    [Fact]
    public void Fuse_UCB_FewerFeedback_GetsHigherExplorationBonus()
    {
        var rarelySeen = Entry("rarelySeen", echo: 0, fizzle: 0);     // denominator 1
        var oftenSeen = Entry("oftenSeen", echo: 40, fizzle: 10);    // denominator 51

        // Identical in both arms so ONLY the UCB term can break the tie.
        var lex = new List<ScoredHit> { new(rarelySeen, 5.0), new(oftenSeen, 5.0) };
        var vec = new List<ScoredHit> { new(rarelySeen, 5.0), new(oftenSeen, 5.0) };

        // TotalN must be > 0 for ln(TotalN+1) > 0 to give UCB any weight.
        var weights = RecallWeights.Default with { TotalN = 50 };
        var fused = RecallScoring.Fuse(lex, vec, weights, Now);

        var rare = fused.Single(f => f.Entry.Id == "rarelySeen");
        var often = fused.Single(f => f.Entry.Id == "oftenSeen");

        Assert.True(rare.Ucb > often.Ucb, $"rare UCB ({rare.Ucb}) should exceed often UCB ({often.Ucb})");
        Assert.True(rare.Fused > often.Fused);
        Assert.Equal("rarelySeen", fused[0].Entry.Id);
    }

    /// <summary>
    /// A4b — with RecallWeights.Default (TotalN=0), ln(TotalN+1)=ln(1)=0, so the UCB term is 0
    /// for every candidate regardless of feedback counts.
    /// </summary>
    [Fact]
    public void Fuse_DefaultWeights_TotalNZero_UcbIsZeroForAll()
    {
        var a = Entry("a", echo: 0, fizzle: 0);
        var b = Entry("b", echo: 99, fizzle: 99);

        var lex = new List<ScoredHit> { new(a, 5.0), new(b, 5.0) };
        var vec = new List<ScoredHit> { new(a, 5.0), new(b, 5.0) };

        var fused = RecallScoring.Fuse(lex, vec, RecallWeights.Default, Now);

        Assert.All(fused, f => Assert.Equal(0.0, f.Ucb));
    }

    /// <summary>
    /// A5 — dual-clock recency through Fuse: an OLD entry (created long ago) with a RECENT
    /// LastAccessedAt out-scores (on the recency component) an equally-old entry whose
    /// LastAccessedAt is null, all arms equal.
    /// </summary>
    [Fact]
    public void Fuse_DualClockRecency_RecentlyAccessedOldEntry_OutscoresStaleOldEntry()
    {
        var longAgo = Now.AddDays(-300);

        var reAccessed = Entry("reAccessed", type: MemoryType.Insight,
            createdAt: longAgo, lastAccessedAt: Now.AddDays(-1));
        var dormant = Entry("dormant", type: MemoryType.Insight,
            createdAt: longAgo, lastAccessedAt: null);

        // Identical arms so ONLY recency differs.
        var lex = new List<ScoredHit> { new(reAccessed, 5.0), new(dormant, 5.0) };
        var vec = new List<ScoredHit> { new(reAccessed, 5.0), new(dormant, 5.0) };

        var fused = RecallScoring.Fuse(lex, vec, RecallWeights.Default, Now);

        var re = fused.Single(f => f.Entry.Id == "reAccessed");
        var dorm = fused.Single(f => f.Entry.Id == "dormant");

        Assert.True(re.Recency > dorm.Recency, $"reAccessed recency ({re.Recency}) should exceed dormant ({dorm.Recency})");
        Assert.True(re.Fused > dorm.Fused);
        Assert.Equal("reAccessed", fused[0].Entry.Id);
    }

    /// <summary>
    /// A5b — direct FadeMemCurve.Recency unit test: null LastAccessedAt falls back to creation-only
    /// and equals the value of an entry whose only clock is creation. Also: in 0..1, and a recent
    /// creation is fresher than an old one.
    /// </summary>
    [Fact]
    public void FadeMemCurve_Recency_NullLastAccessed_FallsBackToCreationOnly()
    {
        var created = Now.AddDays(-45);

        var nullAccess = FadeMemCurve.Recency(created, lastAccessedAt: null, Now, MemoryType.Insight);
        // An explicit lastAccessed equal to creation must yield the same value (max of two equals).
        var equalAccess = FadeMemCurve.Recency(created, lastAccessedAt: created, Now, MemoryType.Insight);
        Assert.Equal(nullAccess, equalAccess, precision: 12);

        // 0..1 range.
        Assert.InRange(nullAccess, 0.0, 1.0);

        // A fresh creation is more recent than the 45-day-old one.
        var fresh = FadeMemCurve.Recency(Now, lastAccessedAt: null, Now, MemoryType.Insight);
        Assert.True(fresh > nullAccess);

        // Dual clock takes the more-recent clock: a recent access lifts an old creation.
        var lifted = FadeMemCurve.Recency(created, lastAccessedAt: Now, Now, MemoryType.Insight);
        Assert.True(lifted > nullAccess, $"recent access ({lifted}) should beat creation-only ({nullAccess})");
        Assert.Equal(fresh, lifted, precision: 12); // accessed now == created now
    }

    /// <summary>
    /// A6 — determinism: two calls with identical inputs yield identical ordering AND identical
    /// component scores.
    /// </summary>
    [Fact]
    public void Fuse_IsDeterministic_AcrossRepeatedCalls()
    {
        var entries = Enumerable.Range(0, 8).Select(i =>
            Entry($"e{i}", echo: i, fizzle: 7 - i, createdAt: Now.AddDays(-i))).ToList();

        var lex = entries.Select((e, i) => new ScoredHit(e, 10.0 - i)).ToList();
        var vec = entries.Select((e, i) => new ScoredHit(e, (i % 3) + 1.0)).ToList();
        var weights = RecallWeights.Default with { TotalN = 30 };

        var first = RecallScoring.Fuse(lex, vec, weights, Now);
        var second = RecallScoring.Fuse(lex, vec, weights, Now);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Entry.Id, second[i].Entry.Id);
            Assert.Equal(first[i].Fused, second[i].Fused);
            Assert.Equal(first[i].Lex, second[i].Lex);
            Assert.Equal(first[i].Vec, second[i].Vec);
            Assert.Equal(first[i].Ucb, second[i].Ucb);
            Assert.Equal(first[i].Recency, second[i].Recency);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Section B — default SearchScoredAsync rank-decay shim
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// B7 — a fake that overrides only FullTextSearchAsync/VectorSearchAsync (the existing
    /// InMemoryEidetStore) gets rank-decayed ScoredHits via the DEFAULT SearchScoredAsync:
    /// the first entity-method result has the highest score and scores strictly decrease.
    /// Confirms the ≈19 existing fakes keep working without opting in.
    /// </summary>
    [Fact]
    public async Task DefaultSearchScored_RankDecays_FirstHitHighest_StrictlyDecreasing()
    {
        var store = new InMemoryEidetStore();
        // Store three matching entries; substring search will return them all.
        await store.StoreAsync(MakeStored("memories/repo-a/insight/1", "kubernetes ingress nginx"));
        await store.StoreAsync(MakeStored("memories/repo-a/insight/2", "kubernetes cluster gke"));
        await store.StoreAsync(MakeStored("memories/repo-a/insight/3", "kubernetes nodes autoscale"));

        var query = new MemoryQuery { Text = "kubernetes", Limit = 10 };
        // Call through the interface so the DEFAULT interface method is exercised.
        IEidetStore asInterface = store;
        var hits = await asInterface.SearchScoredAsync(SearchArm.Lexical, ["repo-a"], query);

        Assert.Equal(3, hits.Count);
        // Rank-decay 1, 1/2, 1/3 → strictly decreasing, first is highest.
        for (var i = 1; i < hits.Count; i++)
            Assert.True(hits[i].Score < hits[i - 1].Score,
                $"score at {i} ({hits[i].Score}) should be < score at {i - 1} ({hits[i - 1].Score})");
        Assert.Equal(1.0, hits[0].Score);
    }

    private static MemoryEntry MakeStored(string id, string content) => new()
    {
        Id = id,
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = content,
        CreatedAt = Now,
        Validity = new Validity { ValidFrom = Now },
        IsLatest = true,
        Importance = 0.5f,
    };

    // ════════════════════════════════════════════════════════════════════════
    // Section C — ExplainRecallAsync diagnostics
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// C8 — ExplainRecallAsync returns one row per candidate with the full component breakdown,
    /// AlphaUsed == 0.5, and CandidatePool == |lex∪vec|.
    /// </summary>
    [Fact]
    public async Task ExplainRecall_ReturnsPerCandidateBreakdown_Alpha05_PoolEqualsUnion()
    {
        var store = new InMemoryScoredStore();
        // lex arm: a, b, c ; vec arm: c, d  →  union {a,b,c,d} = 4
        store.SetArm(SearchArm.Lexical, ("a", 9.0), ("b", 6.0), ("c", 3.0));
        store.SetArm(SearchArm.Vector, ("c", 8.0), ("d", 2.0));
        store.Seed(Entry("a"), Entry("b"), Entry("c"), Entry("d"));

        var svc = new MemoryService(store);
        var explanation = await svc.ExplainRecallAsync("repo-a", new RecallOptions("anything"));

        Assert.Equal(0.5, explanation.AlphaUsed);
        Assert.Equal(4, explanation.CandidatePool);
        Assert.Equal(4, explanation.Rows.Count);

        // Every row carries finite components and the fused total.
        Assert.All(explanation.Rows, row =>
        {
            Assert.False(string.IsNullOrEmpty(row.Id));
            Assert.True(IsFinite(row.Lex) && IsFinite(row.Vec) && IsFinite(row.Recency) && IsFinite(row.Ucb) && IsFinite(row.Fused));
        });

        // The lex-only "a" (highest raw lex) has Lex==1.0 and Vec==0; the vec-only "d" has Vec>0, Lex==0.
        var rowA = explanation.Rows.Single(r => r.Id == "a");
        var rowD = explanation.Rows.Single(r => r.Id == "d");
        Assert.Equal(1.0, rowA.Lex);
        Assert.Equal(0.0, rowA.Vec);
        Assert.Equal(0.0, rowD.Lex);
        Assert.True(rowD.Vec >= 0.0);
    }

    /// <summary>
    /// C9 — ExplainRecallAsync must not consult or poison the recall cache. We Explain first, then
    /// store a new matching memory, then Recall — the new entry must surface. If Explain had warmed
    /// or shared the cache, the subsequent Recall could serve a stale (pre-store) result.
    /// </summary>
    [Fact]
    public async Task ExplainRecall_DoesNotPoisonRecallCache()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Explain over an empty store (no candidates).
        var explained = await svc.ExplainRecallAsync("repo-a", new RecallOptions("redis caching"));
        Assert.Empty(explained.Rows);

        // Store a matching memory AFTER explain.
        var stored = await svc.StoreAsync("repo-a", "redis caching with 5-min ttl", MemoryType.Insight);
        Assert.True(stored.Success);

        // A normal recall must observe the new entry — proving Explain did not seed a stale cache.
        var recalled = await svc.RecallAsync("repo-a", "redis caching");
        Assert.Single(recalled);
        Assert.Equal(stored.Id, recalled[0].Id);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Section D — end-to-end fused ranking through MemoryService.RecallAsync
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// D10 — through MemoryService.RecallAsync backed by scripted per-arm scores, the returned order
    /// reflects FUSED ranking, not insertion/flat order: gold (lexically tied but vector-strong)
    /// ranks above a lexically-tied-but-vector-weak distractor.
    /// </summary>
    [Fact]
    public async Task RecallAsync_OrderReflectsFusedRanking_NotFlatOrInsertionOrder()
    {
        var store = new InMemoryScoredStore();
        // Both gold and distractor have the SAME lexical score (a lexical tie). Under the old flat
        // 1.0 lexical scoring these would be indistinguishable. Gold wins on the vector arm.
        store.SetArm(SearchArm.Lexical, ("distractor", 7.0), ("gold", 7.0));
        store.SetArm(SearchArm.Vector, ("gold", 9.0)); // distractor absent from vec arm
        // Insertion order deliberately puts distractor first.
        store.Seed(Entry("distractor"), Entry("gold"));

        var svc = new MemoryService(store);
        var results = await svc.RecallAsync("repo-a", "query");

        Assert.Equal(2, results.Count);
        Assert.Equal("gold", results[0].Id);   // fused ranking, not insertion order
        Assert.Equal("distractor", results[1].Id);
        Assert.True(results[0].Score > results[1].Score);
    }
}

/// <summary>
/// Test double implementing <see cref="IEidetStore.SearchScoredAsync"/> DIRECTLY with scripted
/// per-arm raw scores — so fusion (not the rank-decay shim) drives ranking. Only the surface
/// <see cref="MemoryService"/> recall/explain touches is implemented; everything else throws or
/// returns empty. Mirrors the in-memory-fake style of <c>MemoryServiceBoundaryTests</c>.
/// </summary>
internal sealed class InMemoryScoredStore : IEidetStore
{
    private readonly Dictionary<SearchArm, List<(string Id, double Score)>> _arms = new()
    {
        [SearchArm.Lexical] = [],
        [SearchArm.Vector] = [],
    };
    private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Script an arm's hits as (id, rawScore) pairs, ordered as the backend would return them.</summary>
    public void SetArm(SearchArm arm, params (string Id, double Score)[] hits) =>
        _arms[arm] = hits.ToList();

    /// <summary>Register the entry objects the scripted ids refer to (carries recency/feedback knobs).</summary>
    public void Seed(params MemoryEntry[] entries)
    {
        foreach (var e in entries) _entries[e.Id] = e;
    }

    public Task<IReadOnlyList<ScoredHit>> SearchScoredAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        // GetValueOrDefault, not the indexer: an arm this test never scripts is an EMPTY arm (which
        // fusion normalizes to 0 for every candidate), not a missing key.
        var hits = (_arms.GetValueOrDefault(arm) ?? [])
            .Where(h => _entries.ContainsKey(h.Id))
            .Select(h => new ScoredHit(_entries[h.Id], h.Score))
            .ToList();
        return Task.FromResult<IReadOnlyList<ScoredHit>>(hits);
    }

    // ── Surface MemoryService.RecallAsync / ExplainRecallAsync also touches ──
    public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        _entries.TryGetValue(id, out var e);
        return Task.FromResult(e);
    }

    public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Id] = entry;
        return Task.FromResult(entry.Id);
    }

    // The fused arms are scripted, so the legacy entity methods are never the source of truth here;
    // they only exist to satisfy the interface (and BumpAccessCounts background path stays harmless).
    public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());
    public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());

    public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }
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
