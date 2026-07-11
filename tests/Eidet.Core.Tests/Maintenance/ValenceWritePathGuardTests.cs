using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The standing regression tripwire for the ValenceSpec correctness core: a positive and a negative
/// claim about the same subject must survive ALL THREE write choke points that would otherwise
/// silently collapse a contradiction (the latent data-loss bug the spec fixes). Each guard is a
/// separate fact; the Cautionary controls prove sign-0 memories still fold normally, so only the
/// hard Affirming↔Refuting pair is protected — not every signed memory.
/// </summary>
public class ValenceWritePathGuardTests
{
    private const string Repo = "repo-a";

    private static MemoryEntry Entry(string id, MemoryType type, string content, float importance, Valence valence)
        => new()
        {
            Id = id,
            RepoId = Repo,
            Type = type,
            Content = content,
            Importance = importance,
            Valence = valence,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        };

    private static MemoryEntry Obs(string idSuffix, string content, Valence valence, float importance = 0.6f)
        => new()
        {
            Id = $"memories/{Repo}/observation/{idSuffix}",
            RepoId = Repo,
            Type = MemoryType.Observation,
            Content = content,
            Valence = valence,
            Tags = ["cache"],
            Importance = importance,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        };

    // ─── Guard #1: store dup-gate (MemoryService.StoreAsync) ──────────────

    [Fact]
    public async Task StoreDupGate_lets_a_refuting_near_duplicate_survive_an_affirming_memory()
    {
        var affirming = Entry($"memories/{Repo}/insight/aff", MemoryType.Insight,
            "Npgsql connection pooling works well under our production load profile", 0.7f, Valence.Affirming);

        // BoostStore.FindDuplicateAsync always returns the seeded entry — the exact "a content-similar
        // memory already exists" condition the dup-gate keys on.
        var store = new BoostStore(affirming);
        var svc = new MemoryService(store);

        // A refuting store that is content-similar to the affirming memory is a CONTRADICTION, not a
        // duplicate — the valence guard must let it through so "X does NOT work" survives alongside "X works".
        var refuting = await svc.StoreAsync(new StoreOptions(Repo,
            "Npgsql connection pooling does NOT work under our production load profile — it deadlocks",
            MemoryType.Insight)
        {
            Valence = Valence.Refuting,
        });

        Assert.True(refuting.Success);
        Assert.NotNull(refuting.Id);
        Assert.Null(refuting.DuplicateId);
        Assert.NotEqual(affirming.Id, refuting.Id);
    }

    [Fact]
    public async Task StoreDupGate_control_still_rejects_a_non_conflicting_near_duplicate()
    {
        var affirming = Entry($"memories/{Repo}/insight/aff", MemoryType.Insight,
            "Npgsql connection pooling works well under our production load profile", 0.7f, Valence.Affirming);

        var store = new BoostStore(affirming);
        var svc = new MemoryService(store);

        // Same (affirming) stance ⇒ genuine duplicate ⇒ still rejected. Proves the gate only spares
        // hard contradictions, not every content-similar store.
        var dup = await svc.StoreAsync(new StoreOptions(Repo,
            "Npgsql connection pooling works nicely under our production load profile",
            MemoryType.Insight)
        {
            Valence = Valence.Affirming,
        });

        Assert.False(dup.Success);
        Assert.Equal(affirming.Id, dup.DuplicateId);
    }

    // ─── Guard #2: dedup (DedupEngine.MergeAsync) ─────────────────────────

    [Fact]
    public async Task Dedup_does_not_merge_an_affirming_and_refuting_near_duplicate()
    {
        var store = new InMemoryEidetStore();
        var affirming = Entry($"memories/{Repo}/insight/aff", MemoryType.Insight,
            "The deployment pipeline runs database migrations before starting the application server",
            0.8f, Valence.Affirming);
        var refuting = Entry($"memories/{Repo}/insight/ref", MemoryType.Insight,
            "The deployment pipeline runs the database migrations before starting the application server",
            0.4f, Valence.Refuting);

        await store.StoreAsync(affirming);
        await store.StoreAsync(refuting);

        // Sanity: absent the valence guard, these lexical near-duplicates (>= 0.85) WOULD merge.
        Assert.True(Eidet.Core.Text.WordSimilarity.Compute(affirming.Content, refuting.Content) >= 0.85f);

        var engine = new DedupEngine(store, new MemoryService(store));
        var result = await engine.DedupAsync(Repo);

        Assert.Equal(0, result.MergedCount);
        // Both remain valid with their distinct stances intact — neither was tombstoned.
        var aff = await store.GetAsync(affirming.Id);
        var ref_ = await store.GetAsync(refuting.Id);
        Assert.Null(aff!.Validity.ValidUntil);
        Assert.Null(ref_!.Validity.ValidUntil);
        Assert.Equal(Valence.Affirming, aff.Valence);
        Assert.Equal(Valence.Refuting, ref_.Valence);
    }

    [Fact]
    public async Task Dedup_control_still_merges_a_cautionary_near_duplicate()
    {
        var store = new InMemoryEidetStore();
        // Cautionary is sign-0, so it does NOT conflict with Affirming and still folds normally.
        var cautionary = Entry($"memories/{Repo}/insight/cau", MemoryType.Insight,
            "The deployment pipeline runs database migrations before starting the application server",
            0.8f, Valence.Cautionary);
        var affirming = Entry($"memories/{Repo}/insight/aff", MemoryType.Insight,
            "The deployment pipeline runs the database migrations before starting the application server",
            0.4f, Valence.Affirming);

        await store.StoreAsync(cautionary);
        await store.StoreAsync(affirming);

        var engine = new DedupEngine(store, new MemoryService(store));
        var result = await engine.DedupAsync(Repo);

        Assert.Equal(1, result.MergedCount);
        var survivor = await store.GetAsync(cautionary.Id);   // higher importance kept
        var discarded = await store.GetAsync(affirming.Id);
        Assert.Null(survivor!.Validity.ValidUntil);
        Assert.NotNull(discarded!.Validity.ValidUntil);
        // Survivor keeps the opinionated (Cautionary) stance via ValencePolarity.Merge.
        Assert.Equal(Valence.Cautionary, survivor.Valence);
    }

    // ─── Guard #3: consolidation (ConsolidationEngine) ────────────────────

    [Fact]
    public async Task Consolidation_partitions_by_sign_and_never_collapses_a_contradiction()
    {
        var store = new InMemoryEidetStore();
        // 3 affirming + 3 refuting observations, all sharing the "cache" tag → one tag-overlap group,
        // partitioned by valence sign into two buckets BEFORE the >=3 threshold. Distinct content so
        // each bucket's representative yields a distinct insight id.
        await store.StoreAsync(Obs("a1", "redis cache warmup cuts p99 latency in half", Valence.Affirming, 0.7f));
        await store.StoreAsync(Obs("a2", "redis cache warmup keeps hit-rate above ninety percent", Valence.Affirming, 0.6f));
        await store.StoreAsync(Obs("a3", "redis cache warmup survives a rolling restart", Valence.Affirming, 0.5f));
        await store.StoreAsync(Obs("r1", "in-process cache warmup exhausts the heap and OOMs the node", Valence.Refuting, 0.7f));
        await store.StoreAsync(Obs("r2", "in-process cache warmup stalls startup past the health-check window", Valence.Refuting, 0.6f));
        await store.StoreAsync(Obs("r3", "in-process cache warmup double-counts entries after a restart", Valence.Refuting, 0.5f));

        var engine = new ConsolidationEngine(store, enrichment: null, new MemoryService(store));
        var result = await engine.ConsolidateAsync(Repo);

        // Two insights, not one: the contradiction is never collapsed into a single stance.
        Assert.Equal(2, result.InsightsCreated);
        var insights = await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight);
        Assert.Equal(2, insights.Count);
        var valences = insights.Select(i => i.Valence).ToHashSet();
        Assert.Contains(Valence.Affirming, valences);
        Assert.Contains(Valence.Refuting, valences);
    }

    [Fact]
    public async Task Consolidation_conflicting_bucket_creates_its_own_insight_instead_of_being_dropped()
    {
        // A standing Affirming insight that FindDuplicateAsync will return for the refuting
        // bucket's representative — the conflict case that used to `continue` and silently
        // drop the negative knowledge forever.
        var existing = Entry($"memories/{Repo}/insight/aff", MemoryType.Insight,
            "cache warmup is safe to enable on every node", 0.7f, Valence.Affirming);
        var store = new BoostStore(existing);

        await store.StoreAsync(Obs("r1", "cache warmup exhausts the heap and OOMs the node", Valence.Refuting, 0.7f));
        await store.StoreAsync(Obs("r2", "cache warmup stalls startup past the health-check window", Valence.Refuting, 0.6f));
        await store.StoreAsync(Obs("r3", "cache warmup double-counts entries after a restart", Valence.Refuting, 0.5f));

        var engine = new ConsolidationEngine(store, enrichment: null, new MemoryService(store));
        var result = await engine.ConsolidateAsync(Repo);

        // The conflicting bucket falls through to CREATE its own Refuting insight, not boost
        // and not vanish — both stances now coexist.
        Assert.Equal(1, result.InsightsCreated);
        Assert.Equal(0, result.InsightsBoosted);

        var insights = await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight);
        var created = Assert.Single(insights, i => i.Id != existing.Id);
        Assert.Equal(Valence.Refuting, created.Valence);

        // The standing Affirming insight is untouched: same importance, no lineage contamination.
        var standing = await store.GetAsync(existing.Id);
        Assert.Equal(0.7f, standing!.Importance);
        Assert.Empty(standing.DerivedFrom);
        Assert.Equal(Valence.Affirming, standing.Valence);
    }

    [Fact]
    public async Task Consolidation_control_folds_a_sign0_bucket_into_one_insight()
    {
        var store = new InMemoryEidetStore();
        // All sign-0 (Neutral + Cautionary) → one bucket → one insight, inheriting the bucket's
        // opinionated (Cautionary) stance. Proves sign-0 memories consolidate freely.
        await store.StoreAsync(Obs("n1", "the cache eviction policy is least-recently-used", Valence.Neutral, 0.7f));
        await store.StoreAsync(Obs("n2", "the cache backing store is a single redis instance", Valence.Neutral, 0.6f));
        await store.StoreAsync(Obs("c1", "the cache silently serves stale reads during failover — watch out", Valence.Cautionary, 0.5f));

        var engine = new ConsolidationEngine(store, enrichment: null, new MemoryService(store));
        var result = await engine.ConsolidateAsync(Repo);

        Assert.Equal(1, result.InsightsCreated);
        var insight = (await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight)).Single();
        Assert.Equal(Valence.Cautionary, insight.Valence);
    }
}
