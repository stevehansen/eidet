using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Behavioral tests for per-repo alpha learning (issue #33 item 6). Each echo/fizzle on a memory that
/// was surfaced under the v2 recall pipeline (so it carries <see cref="MemoryEntry.LastLexShare"/>) is a
/// free relevance label for the lexical-vs-vector blend weight. <see cref="MemoryService.FeedbackAsync"/>
/// folds the label into a per-repo EWMA-learned alpha; <see cref="MemoryService.RecallAsync"/> then ranks
/// with it (clamped, override-beatable).
///
/// The store applies the EWMA fold SERVER-SIDE from its own stored alpha (the post-review design): the
/// service hands it an <see cref="AlphaEwmaUpdate"/> carrying only the relevance target + the recall-domain
/// constants, and the store computes <c>next = clamp((1-λ)·(stored ?? Fallback) + λ·Target, Min, Max)</c>.
/// <see cref="AlphaLearningStore"/> reproduces that fold against an in-memory per-repo dict so the whole
/// feedback→learn→recall loop is exercisable without RavenDB.
/// </summary>
public class AlphaLearningTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    // Mirror of MemoryService's private alpha-learning constants so the tests can assert exact clamps.
    private const double Default = 0.5;   // RecallWeights.Default.Alpha
    private const double Min = 0.15;
    private const double Max = 0.85;

    private static MemoryEntry Entry(
        string id, string repoId = "repo-a", double? lastLexShare = null, int echo = 0, int fizzle = 0) => new()
    {
        Id = id,
        RepoId = repoId,
        Type = MemoryType.Insight,
        Content = id,
        CreatedAt = Now,
        Validity = new Validity { ValidFrom = Now },
        IsLatest = true,
        Importance = 0.5f,
        EchoCount = echo,
        FizzleCount = fizzle,
        LastLexShare = lastLexShare,
    };

    // ════════════════════════════════════════════════════════════════════════
    // Direction of the EWMA nudge
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A high-lexShare (lexically surfaced) memory that ECHOES (was useful) pushes the learned alpha UP
    /// toward the lexical arm — the lexical mix worked, so weight it more.
    /// </summary>
    [Fact]
    public async Task FeedbackAsync_EchoOnLexicalSurfacedMemory_MovesAlphaUp()
    {
        var store = new AlphaLearningStore();
        store.Seed(Entry("m1", lastLexShare: 0.9));

        var before = await store.GetRepoAlphaAsync("repo-a");
        Assert.Null(before); // unlearned to start

        var ok = await new MemoryService(store).FeedbackAsync("m1", wasUsed: true);
        Assert.True(ok);

        var after = await store.GetRepoAlphaAsync("repo-a");
        Assert.NotNull(after);
        // First fold from the 0.5 fallback toward target 0.9 ⇒ strictly above 0.5.
        Assert.True(after > Default, $"alpha after lexical echo ({after}) should exceed default ({Default})");
    }

    /// <summary>
    /// A high-lexShare memory that FIZZLES (misled) pushes the learned alpha DOWN — the lexical mix
    /// misfired, so the fold targets (1 - lexShare), i.e. toward the vector arm.
    /// </summary>
    [Fact]
    public async Task FeedbackAsync_FizzleOnLexicalSurfacedMemory_MovesAlphaDown()
    {
        var store = new AlphaLearningStore();
        store.Seed(Entry("m1", lastLexShare: 0.9));

        await new MemoryService(store).FeedbackAsync("m1", wasUsed: false);

        var after = await store.GetRepoAlphaAsync("repo-a");
        Assert.NotNull(after);
        Assert.True(after < Default, $"alpha after lexical fizzle ({after}) should be below default ({Default})");
    }

    /// <summary>
    /// A vector-surfaced memory (low lexShare) that ECHOES pushes alpha DOWN toward the vector arm —
    /// echo targets the lexShare itself (0.1), which is below the 0.5 fallback.
    /// </summary>
    [Fact]
    public async Task FeedbackAsync_EchoOnVectorSurfacedMemory_MovesAlphaDown()
    {
        var store = new AlphaLearningStore();
        store.Seed(Entry("m1", lastLexShare: 0.1));

        await new MemoryService(store).FeedbackAsync("m1", wasUsed: true);

        var after = await store.GetRepoAlphaAsync("repo-a");
        Assert.NotNull(after);
        Assert.True(after < Default, $"alpha after vector-arm echo ({after}) should be below default ({Default})");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Clamp band — never collapses to a single arm
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Many one-sided echoes on a purely-lexical memory (lexShare 1.0) drive alpha toward — but never
    /// past — the 0.85 ceiling.
    /// </summary>
    [Fact]
    public async Task FeedbackAsync_ManyLexicalEchoes_NeverExceedsAlphaMax()
    {
        var store = new AlphaLearningStore();
        store.Seed(Entry("m1", lastLexShare: 1.0));
        var svc = new MemoryService(store);

        for (var i = 0; i < 50; i++)
            await svc.FeedbackAsync("m1", wasUsed: true);

        var after = await store.GetRepoAlphaAsync("repo-a");
        Assert.NotNull(after);
        Assert.True(after <= Max + 1e-9, $"alpha ({after}) must not exceed AlphaMax ({Max})");
        // And it should have actually climbed up to (essentially) the ceiling.
        Assert.True(after > 0.8, $"50 lexical echoes should drive alpha near the ceiling, got {after}");
    }

    /// <summary>
    /// Many one-sided echoes on a purely-vector memory (lexShare 0.0) drive alpha toward — but never
    /// below — the 0.15 floor.
    /// </summary>
    [Fact]
    public async Task FeedbackAsync_ManyVectorEchoes_NeverDropsBelowAlphaMin()
    {
        var store = new AlphaLearningStore();
        store.Seed(Entry("m1", lastLexShare: 0.0));
        var svc = new MemoryService(store);

        for (var i = 0; i < 50; i++)
            await svc.FeedbackAsync("m1", wasUsed: true);

        var after = await store.GetRepoAlphaAsync("repo-a");
        Assert.NotNull(after);
        Assert.True(after >= Min - 1e-9, $"alpha ({after}) must not drop below AlphaMin ({Min})");
        Assert.True(after < 0.2, $"50 vector echoes should drive alpha near the floor, got {after}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // No-attribution → no-op
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Feedback on a memory with no <see cref="MemoryEntry.LastLexShare"/> (never surfaced under v2)
    /// records the echo but performs NO alpha update — there is no relevance label to attribute.
    /// </summary>
    [Fact]
    public async Task FeedbackAsync_NoLexShareAttribution_DoesNotTouchAlpha()
    {
        var store = new AlphaLearningStore();
        store.Seed(Entry("m1", lastLexShare: null, echo: 0));

        var ok = await new MemoryService(store).FeedbackAsync("m1", wasUsed: true);

        Assert.True(ok);                                  // feedback itself succeeds
        Assert.Equal(1, store.GetEntry("m1")!.EchoCount); // echo was recorded
        Assert.Equal(0, store.AlphaUpdateCalls);          // but no EWMA step ran
        Assert.Null(await store.GetRepoAlphaAsync("repo-a"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // Fallback fold from the cold-start default, not 0
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The first-ever feedback for a repo with no stored alpha folds from the cold-start default (0.5),
    /// not from 0. A neutral lexShare (0.5) echo therefore leaves alpha exactly at 0.5 — proving the
    /// fold base is the fallback. (Folding from 0 would land at λ·0.5 = 0.05.)
    /// </summary>
    [Fact]
    public async Task FeedbackAsync_FirstFeedback_FoldsFromDefaultNotZero()
    {
        var store = new AlphaLearningStore();
        store.Seed(Entry("m1", lastLexShare: 0.5)); // neutral target == fallback

        await new MemoryService(store).FeedbackAsync("m1", wasUsed: true);

        var after = await store.GetRepoAlphaAsync("repo-a");
        Assert.NotNull(after);
        // next = (1-λ)·0.5 + λ·0.5 = 0.5 exactly — only true if the base was the 0.5 fallback.
        Assert.Equal(0.5, after!.Value, precision: 9);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Recall applies the learned alpha
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// With a learned alpha of 0.8 (lexical-leaning), a lexically-strong candidate out-ranks a
    /// vector-strong one; flip the learned alpha to 0.2 (vector-leaning) and the ordering reverses.
    /// Drives the whole RecallAsync path so the learned weight visibly changes results.
    /// </summary>
    [Fact]
    public async Task RecallAsync_OrderingFollowsLearnedAlpha_FlipsWithTheWeight()
    {
        // lexHero is the lexical-arm leader; vecHero is the vector-arm leader. Equal-but-opposite.
        async Task<List<MemorySearchResult>> RecallWithAlpha(double alpha)
        {
            var store = new AlphaLearningStore();
            store.SetAlpha("repo-a", alpha);
            store.SetArm(SearchArm.Lexical, ("lexHero", 10.0), ("vecHero", 5.0));
            store.SetArm(SearchArm.Vector, ("vecHero", 10.0), ("lexHero", 5.0));
            store.Seed(Entry("lexHero"), Entry("vecHero"));
            // ExpandGraph off so neighbor expansion can't perturb the pure-fusion ordering.
            return await new MemoryService(store).RecallAsync(
                "repo-a", new RecallOptions("q") { ExpandGraph = false });
        }

        var lexLeaning = await RecallWithAlpha(0.8);
        var vecLeaning = await RecallWithAlpha(0.2);

        Assert.Equal("lexHero", lexLeaning[0].Id);  // alpha 0.8 favors the lexical arm
        Assert.Equal("vecHero", vecLeaning[0].Id);  // alpha 0.2 favors the vector arm
    }

    /// <summary>
    /// <see cref="ExplainRecallAsync"/> reflects the learned alpha (0.7) in <c>AlphaUsed</c>, clamped
    /// into the band — proving recall resolves and ranks with the per-repo learned weight.
    /// </summary>
    [Fact]
    public async Task ExplainRecall_AlphaUsed_ReflectsLearnedValue()
    {
        var store = new AlphaLearningStore();
        store.SetAlpha("repo-a", 0.7);
        store.SetArm(SearchArm.Lexical, ("a", 5.0));
        store.SetArm(SearchArm.Vector, ("a", 5.0));
        store.Seed(Entry("a"));

        var explanation = await new MemoryService(store).ExplainRecallAsync("repo-a", new RecallOptions("q"));

        Assert.Equal(0.7, explanation.AlphaUsed, precision: 9);
    }

    /// <summary>
    /// A learned alpha that sits OUTSIDE the [0.15, 0.85] band is clamped at read-time. A stored 0.95 is
    /// reported by ExplainRecall as 0.85 (the ceiling).
    /// </summary>
    [Fact]
    public async Task ExplainRecall_AlphaUsed_ClampsOutOfBandLearnedValue()
    {
        var store = new AlphaLearningStore();
        store.SetAlpha("repo-a", 0.95); // above the ceiling
        store.SetArm(SearchArm.Lexical, ("a", 5.0));
        store.SetArm(SearchArm.Vector, ("a", 5.0));
        store.Seed(Entry("a"));

        var explanation = await new MemoryService(store).ExplainRecallAsync("repo-a", new RecallOptions("q"));

        Assert.Equal(Max, explanation.AlphaUsed, precision: 9);
    }

    // ════════════════════════════════════════════════════════════════════════
    // AlphaOverride beats the learned alpha
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An explicit <see cref="RecallOptions.AlphaOverride"/> wins over the per-repo learned alpha
    /// (still clamped into band). The store has learned 0.8 but the override pins 0.3.
    /// </summary>
    [Fact]
    public async Task ExplainRecall_AlphaOverride_BeatsLearnedAlpha()
    {
        var store = new AlphaLearningStore();
        store.SetAlpha("repo-a", 0.8);
        store.SetArm(SearchArm.Lexical, ("a", 5.0));
        store.SetArm(SearchArm.Vector, ("a", 5.0));
        store.Seed(Entry("a"));

        var explanation = await new MemoryService(store).ExplainRecallAsync(
            "repo-a", new RecallOptions("q") { AlphaOverride = 0.3 });

        Assert.Equal(0.3, explanation.AlphaUsed, precision: 9);
    }

    /// <summary>An out-of-band override is clamped too: AlphaOverride=0.05 → 0.15 (the floor).</summary>
    [Fact]
    public async Task ExplainRecall_AlphaOverride_IsClampedIntoBand()
    {
        var store = new AlphaLearningStore();
        store.SetArm(SearchArm.Lexical, ("a", 5.0));
        store.SetArm(SearchArm.Vector, ("a", 5.0));
        store.Seed(Entry("a"));

        var explanation = await new MemoryService(store).ExplainRecallAsync(
            "repo-a", new RecallOptions("q") { AlphaOverride = 0.05 });

        Assert.Equal(Min, explanation.AlphaUsed, precision: 9);
    }

    // ════════════════════════════════════════════════════════════════════════
    // End-to-end loop: recall stamps lexShare → feedback learns from it
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The strongest test — the whole attribution loop wired through the real service. A recall surfaces
    /// a purely-lexical local hit, which stamps <c>LastLexShare</c> ≈ 1.0 via the access-tracking patch.
    /// A subsequent echo on that id then folds the learned alpha UP toward lexical — proving the recall
    /// side stamps the share AND the feedback side reads it. No hand-set lexShare anywhere.
    /// </summary>
    [Fact]
    public async Task RecallThenEcho_StampsLexShareAndLearnsFromIt()
    {
        var store = new AlphaLearningStore();
        // Purely lexical surfacing: present in the lexical arm only ⇒ lexShare stamps to ~1.0.
        store.SetArm(SearchArm.Lexical, ("m1", 7.0));
        store.SetArm(SearchArm.Vector); // empty vector arm
        store.Seed(Entry("m1", lastLexShare: null));
        var svc = new MemoryService(store);

        // 1) Recall surfaces m1 and (via PatchAccessAsync) stamps its LastLexShare.
        var results = await svc.RecallAsync("repo-a", "q");
        Assert.Contains(results, r => r.Id == "m1");

        // The recall side-effect runs fire-and-forget; give it a moment to land the patch.
        await WaitUntilAsync(() => store.GetEntry("m1")!.LastLexShare is not null);
        var stamped = store.GetEntry("m1")!.LastLexShare;
        Assert.NotNull(stamped);
        Assert.True(stamped > 0.9, $"lex-only surfacing should stamp lexShare near 1.0, got {stamped}");

        // 2) An echo on the now-attributed memory learns alpha UP toward lexical.
        Assert.Null(await store.GetRepoAlphaAsync("repo-a"));
        await svc.FeedbackAsync("m1", wasUsed: true);

        var learned = await store.GetRepoAlphaAsync("repo-a");
        Assert.NotNull(learned);
        Assert.True(learned > Default, $"echo on a lex-surfaced hit should lift alpha above default, got {learned}");
    }

    /// <summary>
    /// lexShare prior: a candidate scored by NEITHER arm (it rode in via graph expansion) is stamped with
    /// the deliberate no-arm-info prior 0.5, not 0 or 1. We surface a linked neighbor that is in neither
    /// arm, then assert the stamped LastLexShare is exactly 0.5.
    /// </summary>
    [Fact]
    public async Task Recall_NoArmCandidate_StampsLexSharePriorOfHalf()
    {
        var store = new AlphaLearningStore();
        // parent is the only arm hit; neighbor is in NO arm but is link-reachable from parent.
        var parent = Entry("parent", lastLexShare: null);
        parent.Links.Add(new MemoryLink { TargetRepoId = "repo-a", TargetMemoryId = "neighbor", Relation = "supports" });
        var neighbor = Entry("neighbor", lastLexShare: null);

        store.SetArm(SearchArm.Lexical, ("parent", 7.0));
        store.SetArm(SearchArm.Vector, ("parent", 7.0));
        store.Seed(parent, neighbor);
        var svc = new MemoryService(store);

        var results = await svc.RecallAsync("repo-a", "q"); // ExpandGraph default on
        Assert.Contains(results, r => r.Id == "neighbor");

        await WaitUntilAsync(() => store.GetEntry("neighbor")!.LastLexShare is not null);
        Assert.Equal(0.5, store.GetEntry("neighbor")!.LastLexShare!.Value, precision: 9);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}

/// <summary>
/// Test double that faithfully models the SERVER-SIDE EWMA fold and the lexShare-stamping access patch.
/// Scripted per-arm scores drive fusion (like <c>InMemoryScoredStore</c>); on top of that it keeps a
/// per-repo alpha dict and applies the <see cref="AlphaEwmaUpdate"/> fold inside
/// <see cref="UpdateRepoAlphaAsync"/> exactly as the real Raven patch would, and records lexShare onto
/// the in-memory entry in <see cref="PatchAccessAsync"/> so the feedback loop is end-to-end testable.
/// </summary>
internal sealed class AlphaLearningStore : IEidetStore
{
    private readonly Dictionary<SearchArm, List<(string Id, double Score)>> _arms = new()
    {
        [SearchArm.Lexical] = [],
        [SearchArm.Vector] = [],
    };
    private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _alpha = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times the server-side fold was invoked — lets a test assert the no-op contract.</summary>
    public int AlphaUpdateCalls { get; private set; }

    public void SetArm(SearchArm arm, params (string Id, double Score)[] hits) => _arms[arm] = hits.ToList();

    public void Seed(params MemoryEntry[] entries)
    {
        foreach (var e in entries) _entries[e.Id] = e;
    }

    public void SetAlpha(string repoId, double alpha) => _alpha[repoId] = alpha;

    public MemoryEntry? GetEntry(string id) => _entries.GetValueOrDefault(id);

    public Task<IReadOnlyList<ScoredHit>> SearchScoredAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        var hits = _arms[arm]
            .Where(h => _entries.ContainsKey(h.Id) && repoIds.Contains(_entries[h.Id].RepoId, StringComparer.OrdinalIgnoreCase))
            .Select(h => new ScoredHit(_entries[h.Id], h.Score))
            .ToList();
        return Task.FromResult<IReadOnlyList<ScoredHit>>(hits);
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

    public Task<double?> GetRepoAlphaAsync(string repoId, CancellationToken ct = default) =>
        Task.FromResult(_alpha.TryGetValue(repoId, out var a) ? a : (double?)null);

    /// <summary>Applies the fold server-side from the stored value (or the supplied fallback when unlearned).</summary>
    public Task UpdateRepoAlphaAsync(string repoId, AlphaEwmaUpdate u, CancellationToken ct = default)
    {
        AlphaUpdateCalls++;
        var current = _alpha.TryGetValue(repoId, out var a) ? a : u.Fallback;
        var next = Math.Clamp((1 - u.Lambda) * current + u.Lambda * u.Target, u.Min, u.Max);
        _alpha[repoId] = next;
        return Task.CompletedTask;
    }

    /// <summary>Records the surfacing lexShare (and access fields) onto the in-memory entry.</summary>
    public Task PatchAccessAsync(string entryId, DateTime lastAccessedAt, double? lexShare = null, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(entryId, out var e))
        {
            e.AccessCount++;
            e.LastAccessedAt = lastAccessedAt;
            if (lexShare is { } s) e.LastLexShare = s;
        }
        return Task.CompletedTask;
    }

    // ── Unused interface surface (recall/feedback never reach these here) ──
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
