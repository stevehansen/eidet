using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Storage;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Contract tests for <see cref="DedupEngine"/>. Two candidate sources are exercised
/// independently: the in-process lexical Jaccard pass (no embeddings needed — the
/// default <see cref="InMemoryEidetStore"/> returns [] from FindNearDuplicatesAsync)
/// and the semantic pass (driven by <see cref="SemanticDedupStore"/>, a focused fake
/// whose FindNearDuplicatesAsync returns seeded near-duplicates).
/// </summary>
public class DedupEngineTests
{
    private static MemoryEntry Entry(
        string id, MemoryType type, string content, float importance,
        int accessCount = 0, IEnumerable<string>? tags = null, string repoId = "repo-a")
    {
        return new MemoryEntry
        {
            Id = id,
            RepoId = repoId,
            Type = type,
            Content = content,
            Importance = importance,
            AccessCount = accessCount,
            Tags = tags?.ToList() ?? [],
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        };
    }

    // ─── 1. Lexical near-duplicates merge ─────────────────────────────

    [Fact]
    public async Task Lexical_near_duplicates_merge_keeping_higher_importance()
    {
        var store = new InMemoryEidetStore();

        // Near-identical wording: Jaccard well above the 0.85 lexical threshold.
        var high = Entry("memories/repo-a/insight/high", MemoryType.Insight,
            "The deployment pipeline runs database migrations before starting the application server",
            importance: 0.8f, accessCount: 3, tags: ["deploy", "db"]);
        var low = Entry("memories/repo-a/insight/low", MemoryType.Insight,
            "The deployment pipeline runs the database migrations before starting the application server",
            importance: 0.4f, accessCount: 5, tags: ["DEPLOY", "migrations"]);

        await store.StoreAsync(high);
        await store.StoreAsync(low);

        var engine = new DedupEngine(store);
        var result = await engine.DedupAsync("repo-a");

        Assert.Equal(1, result.MergedCount);
        var pair = Assert.Single(result.Merges);
        Assert.Equal(high.Id, pair.KeptId);
        Assert.Equal(low.Id, pair.DiscardedId);

        var keptStored = await store.GetAsync(high.Id);
        var discardedStored = await store.GetAsync(low.Id);

        // Higher-importance entry kept and untouched on validity.
        Assert.Null(keptStored!.Validity.ValidUntil);
        // AccessCount summed onto the survivor: 3 + 5.
        Assert.Equal(8, keptStored.AccessCount);
        // Tags unioned case-insensitively, no dupes (deploy retained once, migrations added).
        Assert.Equal(3, keptStored.Tags.Count);
        Assert.Contains("deploy", keptStored.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("db", keptStored.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("migrations", keptStored.Tags, StringComparer.OrdinalIgnoreCase);

        // Lower-importance entry discarded: ValidUntil set + ForgetReason references the survivor.
        Assert.NotNull(discardedStored!.Validity.ValidUntil);
        Assert.Equal($"Dedup merged into {high.Id}", discardedStored.ForgetReason);
    }

    // ─── 2. Semantic paraphrase merge (the headline regression) ───────

    [Fact]
    public async Task Semantic_paraphrase_merges_what_lexical_pass_misses()
    {
        // Two paraphrases with deliberately LOW word overlap — the lexical pass
        // (Jaccard >= 0.85) cannot see them as duplicates.
        var a = Entry("memories/repo-a/insight/a", MemoryType.Insight,
            "Authentication relies on short-lived signed tokens issued per session",
            importance: 0.7f, accessCount: 2, tags: ["auth"]);
        var b = Entry("memories/repo-a/insight/b", MemoryType.Insight,
            "Login security depends upon ephemeral cryptographic credentials granted each visit",
            importance: 0.3f, accessCount: 4, tags: ["security"]);

        // Sanity: confirm the lexical pass really would NOT catch this pair.
        Assert.True(Eidet.Core.Text.WordSimilarity.Compute(a.Content, b.Content) < 0.85f);

        var store = new SemanticDedupStore();
        await store.StoreAsync(a);
        await store.StoreAsync(b);
        // The vector index "knows" b is a near-dup of a.
        store.SeedNearDuplicate(a.Id, b.Id);

        var engine = new DedupEngine(store);
        var result = await engine.DedupAsync("repo-a");

        Assert.Equal(1, result.MergedCount);
        var pair = Assert.Single(result.Merges);
        Assert.Equal(a.Id, pair.KeptId);       // higher importance kept
        Assert.Equal(b.Id, pair.DiscardedId);

        var kept = await store.GetAsync(a.Id);
        var discarded = await store.GetAsync(b.Id);
        Assert.Null(kept!.Validity.ValidUntil);
        Assert.Equal(6, kept.AccessCount);     // 2 + 4
        Assert.Contains("security", kept.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(discarded!.Validity.ValidUntil);
        Assert.Equal($"Dedup merged into {a.Id}", discarded.ForgetReason);
    }

    // ─── 3. Different memory types do NOT merge ───────────────────────

    [Fact]
    public async Task Different_memory_types_do_not_merge()
    {
        var store = new SemanticDedupStore();

        var observation = Entry("memories/repo-a/observation/x", MemoryType.Observation,
            "The deployment pipeline runs database migrations before starting the application server",
            importance: 0.8f);
        var insight = Entry("memories/repo-a/insight/x", MemoryType.Insight,
            "The deployment pipeline runs database migrations before starting the application server",
            importance: 0.6f);

        await store.StoreAsync(observation);
        await store.StoreAsync(insight);
        // Even if the index were asked, cross-type seeding must not bridge them.
        store.SeedNearDuplicate(observation.Id, insight.Id);

        var engine = new DedupEngine(store);
        var result = await engine.DedupAsync("repo-a");

        Assert.Equal(0, result.MergedCount);
        Assert.Null((await store.GetAsync(observation.Id))!.Validity.ValidUntil);
        Assert.Null((await store.GetAsync(insight.Id))!.Validity.ValidUntil);
    }

    // ─── 4. dryRun performs no writes ─────────────────────────────────

    [Fact]
    public async Task DryRun_reports_merges_without_mutating_store()
    {
        var store = new CountingDedupStore();

        var high = Entry("memories/repo-a/insight/high", MemoryType.Insight,
            "The deployment pipeline runs database migrations before starting the application server",
            importance: 0.8f, accessCount: 3);
        var low = Entry("memories/repo-a/insight/low", MemoryType.Insight,
            "The deployment pipeline runs the database migrations before starting the application server",
            importance: 0.4f, accessCount: 5);

        await store.StoreAsync(high);
        await store.StoreAsync(low);

        var engine = new DedupEngine(store);
        var result = await engine.DedupAsync("repo-a", dryRun: true);

        // The would-merge pair is reported.
        Assert.Equal(1, result.MergedCount);
        Assert.Equal(high.Id, result.Merges[0].KeptId);

        // Persistence-layer contract: no write reaches the store in a dry run.
        // (The engine mutates the in-memory candidate objects in place regardless;
        // with a real store those edits are never persisted because UpdateAsync is
        // skipped, so asserting on UpdateAsync calls is the contract-accurate check.)
        Assert.Equal(0, store.UpdateCalls);
    }

    // ─── 5. Idempotency / no self-merge / claimed-set collapse ────────

    [Fact]
    public async Task Single_entry_with_no_duplicates_yields_no_merges()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Entry("memories/repo-a/insight/solo", MemoryType.Insight,
            "The cache layer evicts least-recently-used entries on memory pressure",
            importance: 0.6f));

        var engine = new DedupEngine(store);
        var result = await engine.DedupAsync("repo-a");

        Assert.Equal(0, result.MergedCount);
    }

    [Fact]
    public async Task Three_mutually_similar_entries_collapse_to_one_survivor()
    {
        var store = new InMemoryEidetStore();

        // Three near-identical entries — all mutually above the lexical threshold.
        var e1 = Entry("memories/repo-a/insight/e1", MemoryType.Insight,
            "The deployment pipeline runs database migrations before starting the application server",
            importance: 0.9f);
        var e2 = Entry("memories/repo-a/insight/e2", MemoryType.Insight,
            "The deployment pipeline runs the database migrations before starting the application server",
            importance: 0.5f);
        var e3 = Entry("memories/repo-a/insight/e3", MemoryType.Insight,
            "The deployment pipeline now runs the database migrations before starting the application server",
            importance: 0.3f);

        await store.StoreAsync(e1);
        await store.StoreAsync(e2);
        await store.StoreAsync(e3);

        var engine = new DedupEngine(store);
        var result = await engine.DedupAsync("repo-a");

        // Two merges fold e2 and e3 away; no entry is both kept and discarded.
        Assert.Equal(2, result.MergedCount);
        var keptIds = result.Merges.Select(m => m.KeptId).ToHashSet();
        var discardedIds = result.Merges.Select(m => m.DiscardedId).ToHashSet();
        Assert.Empty(keptIds.Intersect(discardedIds));

        // Exactly one entry remains valid (the highest-importance survivor, e1).
        var survivors = new[] { e1.Id, e2.Id, e3.Id }
            .Select(id => store.GetAsync(id).Result!)
            .Where(e => e.Validity.ValidUntil is null)
            .ToList();
        var survivor = Assert.Single(survivors);
        Assert.Equal(e1.Id, survivor.Id);
    }

    // ─── 6. DedupOptions.Types filter ─────────────────────────────────

    [Fact]
    public async Task Types_filter_restricts_sweep_to_named_types()
    {
        var store = new InMemoryEidetStore();

        var insightHigh = Entry("memories/repo-a/insight/high", MemoryType.Insight,
            "The deployment pipeline runs database migrations before starting the application server",
            importance: 0.8f);
        var insightLow = Entry("memories/repo-a/insight/low", MemoryType.Insight,
            "The deployment pipeline runs the database migrations before starting the application server",
            importance: 0.4f);
        var obsHigh = Entry("memories/repo-a/observation/high", MemoryType.Observation,
            "The deployment pipeline runs database migrations before starting the application server",
            importance: 0.8f);
        var obsLow = Entry("memories/repo-a/observation/low", MemoryType.Observation,
            "The deployment pipeline runs the database migrations before starting the application server",
            importance: 0.4f);

        await store.StoreAsync(insightHigh);
        await store.StoreAsync(insightLow);
        await store.StoreAsync(obsHigh);
        await store.StoreAsync(obsLow);

        var engine = new DedupEngine(store);
        var result = await engine.DedupAsync("repo-a",
            new DedupOptions { Types = [MemoryType.Insight] });

        // Only the Insight pair merges; the Observation pair is untouched.
        Assert.Equal(1, result.MergedCount);
        Assert.Equal(insightHigh.Id, result.Merges[0].KeptId);
        Assert.NotNull((await store.GetAsync(insightLow.Id))!.Validity.ValidUntil);
        Assert.Null((await store.GetAsync(obsLow.Id))!.Validity.ValidUntil);
    }

    // ─── 7. Regression: a folded-away entry must not keep merging (semantic pass) ───

    [Fact]
    public async Task Folded_entry_does_not_merge_remaining_candidates_into_a_tombstone()
    {
        // GetTopScoredAsync orders importance-desc: high, mid, low. Distinct content so the
        // lexical pass stays silent and only the semantic pass drives merges.
        var high = Entry("memories/repo-a/insight/high", MemoryType.Insight,
            "Authentication uses short-lived signed session tokens", importance: 0.9f);
        var mid = Entry("memories/repo-a/insight/mid", MemoryType.Insight,
            "Background jobs are scheduled through a persistent queue", importance: 0.5f);
        var low = Entry("memories/repo-a/insight/low", MemoryType.Insight,
            "The user profile screen lazily loads avatar images", importance: 0.2f);

        var store = new SemanticDedupStore();
        await store.StoreAsync(high);
        await store.StoreAsync(mid);
        await store.StoreAsync(low);

        // mid's vector neighbours are BOTH high (which outranks mid) and low (which mid outranks).
        // The first merge folds mid into high; low must NOT then be merged into the tombstoned mid.
        store.SeedNearDuplicate(mid.Id, high.Id);
        store.SeedNearDuplicate(mid.Id, low.Id);

        var engine = new DedupEngine(store);
        var result = await engine.DedupAsync("repo-a");

        // Exactly one merge: mid folds into high; low is untouched.
        Assert.Equal(1, result.MergedCount);
        var pair = Assert.Single(result.Merges);
        Assert.Equal(high.Id, pair.KeptId);
        Assert.Equal(mid.Id, pair.DiscardedId);

        // The invariant the bug violated: no entry is both a survivor and a discard.
        var keptIds = result.Merges.Select(m => m.KeptId).ToHashSet();
        var discardedIds = result.Merges.Select(m => m.DiscardedId).ToHashSet();
        Assert.Empty(keptIds.Intersect(discardedIds));

        // low never folded into the tombstoned mid — still valid.
        Assert.Null((await store.GetAsync(low.Id))!.Validity.ValidUntil);
    }
}

/// <summary>
/// Extends <see cref="InMemoryEidetStore"/> with a seedable semantic index so the
/// dedup engine's semantic pass can be driven without real embeddings. A seeded
/// near-dup is only returned when the queried entry's type matches the candidate's
/// type, mirroring the Raven implementation's per-type vector query.
/// </summary>
internal sealed class SemanticDedupStore : InMemoryEidetStore
{
    private readonly Dictionary<string, List<string>> _nearDups = new(StringComparer.OrdinalIgnoreCase);

    public void SeedNearDuplicate(string entryId, string nearDupId)
    {
        if (!_nearDups.TryGetValue(entryId, out var list))
            _nearDups[entryId] = list = [];
        list.Add(nearDupId);
    }

    public override async Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default)
    {
        if (!_nearDups.TryGetValue(entry.Id, out var ids)) return [];

        var results = new List<MemoryEntry>();
        foreach (var id in ids)
        {
            var cand = await GetAsync(id, ct);
            if (cand is null || cand.Id == entry.Id) continue;
            if (cand.Type != entry.Type) continue;          // per-type vector query
            if (cand.Validity.ValidUntil is not null) continue;
            results.Add(cand);
        }
        return results.Take(max).ToList();
    }
}

/// <summary>Counts <see cref="IEidetStore.UpdateAsync"/> calls so dry-run can be verified at the persistence boundary.</summary>
internal sealed class CountingDedupStore : InMemoryEidetStore
{
    public int UpdateCalls { get; private set; }

    public override Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        UpdateCalls++;
        return base.UpdateAsync(entry, ct);
    }
}
