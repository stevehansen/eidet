using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Tests.Maintenance;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Canon;

/// <summary>
/// Regressions for the companion edits that ship with Canon P1 (issue #75): the additive
/// <c>StoreOptions.DerivedFrom</c> carry in <see cref="WriteValidator.BuildEntry"/>, and the
/// <c>canon:*</c> exclusion guards in <see cref="ConsolidationEngine"/> (boost path) and
/// <see cref="DedupEngine"/> — a curated Canon page must never be boosted, contaminated, or merged away by
/// the unsupervised maintenance stages.
/// </summary>
public class CanonCompanionEditTests
{
    // ─── WriteValidator.BuildEntry — DerivedFrom carry ──────────────────

    [Fact]
    public void BuildEntry_CarriesDerivedFrom_AndDefaultsEmptyWhenNull()
    {
        const string content = "Auth uses JWT RS256 with a ten-minute token TTL and rotation on use";

        var withLineage = WriteValidator.BuildEntry(
            new StoreOptions("repo-a", content, MemoryType.Insight)
            {
                DerivedFrom = ["memories/repo-a/insight/a", "memories/repo-a/insight/b"],
            });
        Assert.True(withLineage.IsBuilt);
        Assert.Equal(
            new[] { "memories/repo-a/insight/a", "memories/repo-a/insight/b" },
            withLineage.Entry!.DerivedFrom);

        // Null DerivedFrom defaults to an empty (non-null) list — backward compatible for every caller.
        var withoutLineage = WriteValidator.BuildEntry(
            new StoreOptions("repo-a", content, MemoryType.Insight));
        Assert.True(withoutLineage.IsBuilt);
        Assert.NotNull(withoutLineage.Entry!.DerivedFrom);
        Assert.Empty(withoutLineage.Entry.DerivedFrom);
    }

    // ─── ConsolidationEngine — boost path skips a canon:* page ──────────

    [Fact]
    public async Task Consolidation_BoostPath_SkipsCanonTaggedInsight_CreatesFreshInstead()
    {
        // A canon:* page standing in as the boost "duplicate". But for its canon tag, three trusted
        // tag-overlapping observations would boost it; the guard must skip the boost and fall through to
        // create a fresh (non-canon) insight — never lifting or contaminating the curated page.
        var canonInsight = new MemoryEntry
        {
            Id = "memories/repo-a/insight/canon-deploy",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "the deployment pipeline is well understood",
            Tags = ["deploy", "canon:term:deploy"],
            Provenance = MemoryProvenance.Consolidation,
            Importance = 0.6f,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        };
        var store = new BoostStore(canonInsight);

        await store.StoreAsync(Observation("a", "the deployment pipeline is well understood now"));
        await store.StoreAsync(Observation("b", "the deployment pipeline is well understood today"));
        await store.StoreAsync(Observation("c", "the deployment pipeline is well understood here"));

        var importanceBefore = canonInsight.Importance;
        var derivedBefore = canonInsight.DerivedFrom.Count;

        var engine = new ConsolidationEngine(store, enrichment: null, new MemoryService(store));
        var result = await engine.ConsolidateAsync("repo-a");

        // The canon page was NOT boosted; its importance and lineage are untouched...
        Assert.Equal(0, result.InsightsBoosted);
        Assert.Equal(importanceBefore, canonInsight.Importance);
        Assert.Equal(derivedBefore, canonInsight.DerivedFrom.Count);
        // ...and the bucket fell through to create a fresh insight (proving the guard, not a dropped bucket).
        Assert.Equal(1, result.InsightsCreated);
    }

    // ─── DedupEngine — never merges a canon:* page ──────────────────────

    [Fact]
    public async Task Dedup_NeverMergesCanonTaggedEntry_ButStillMergesPlainNearDuplicates()
    {
        var store = new InMemoryEidetStore();

        // But for its canon tag, canonPage is the highest-importance near-duplicate — it would be the
        // survivor and would absorb the two plain near-dups' tags. The guard excludes it entirely.
        var canonPage = DedupEntry("memories/repo-a/insight/canon-deploy", 0.9f,
            "The deployment pipeline runs database migrations before starting the application server",
            ["deploy", "canon:term:deploy"]);
        var plainHigh = DedupEntry("memories/repo-a/insight/high", 0.8f,
            "The deployment pipeline runs the database migrations before starting the application server",
            ["deploy"]);
        var plainLow = DedupEntry("memories/repo-a/insight/low", 0.4f,
            "The deployment pipeline now runs the database migrations before starting the application server",
            ["deploy"]);
        await store.StoreAsync(canonPage);
        await store.StoreAsync(plainHigh);
        await store.StoreAsync(plainLow);

        var engine = new DedupEngine(store, new MemoryService(store));
        var result = await engine.DedupAsync("repo-a");

        // Exactly one merge — the two PLAIN near-dups — and the canon page is in neither pair.
        Assert.Equal(1, result.MergedCount);
        Assert.DoesNotContain(result.Merges, p => p.KeptId == canonPage.Id || p.DiscardedId == canonPage.Id);

        // The canon page is untouched: still live, with its tags uncontaminated by the merge.
        var storedCanon = await store.GetAsync(canonPage.Id);
        Assert.Null(storedCanon!.Validity.ValidUntil);
        Assert.Equal(2, storedCanon.Tags.Count);   // exactly [deploy, canon:term:deploy]
    }

    // ─── MemoryService — supersession exempt from the dedup short-circuit ─

    [Fact]
    public async Task Store_SupersedingItsOwnNearIdenticalTarget_BypassesDedup_OtherMatchesStillDedupe()
    {
        var store = new CannedDuplicateStore();
        var memory = new MemoryService(store);

        var incumbent = await memory.StoreAsync(new StoreOptions("repo-a",
            "The deployment pipeline runs database migrations before starting the application server",
            MemoryType.Insight));
        Assert.True(incumbent.Success);
        store.Duplicate = await store.GetAsync(incumbent.Id!);   // every later store now "matches" it

        // Near-identical content WITHOUT Supersedes still dedupes (unchanged behavior).
        var plain = await memory.StoreAsync(new StoreOptions("repo-a",
            "The deployment pipeline runs the database migrations before starting the application server",
            MemoryType.Insight));
        Assert.False(plain.Success);
        Assert.Equal(incumbent.Id, plain.DuplicateId);

        // Superseding the MATCH ITSELF is a correction, not a duplicate — the canon re-approve shape
        // (a page re-minted after a small edit is, by construction, near-identical to its target).
        var correction = await memory.StoreAsync(new StoreOptions("repo-a",
            "The deployment pipeline runs the database migrations before starting the application server",
            MemoryType.Insight)
        {
            Supersedes = incumbent.Id,
        });
        Assert.True(correction.Success);
        Assert.NotNull(correction.Id);

        // Superseding some OTHER memory while matching the incumbent still dedupes.
        var other = await memory.StoreAsync(new StoreOptions("repo-a",
            "The deployment pipeline runs all database migrations before starting the application server",
            MemoryType.Insight)
        {
            Supersedes = "memories/repo-a/insight/unrelated",
        });
        Assert.False(other.Success);
        Assert.Equal(incumbent.Id, other.DuplicateId);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>In-memory store whose <c>FindDuplicateAsync</c> returns a canned incumbent (the
    /// <c>BoostStore</c> pattern) — the base fake never reports duplicates.</summary>
    private sealed class CannedDuplicateStore : InMemoryEidetStore
    {
        public MemoryEntry? Duplicate { get; set; }

        public override Task<MemoryEntry?> FindDuplicateAsync(
            string repoId, string content, float threshold, CancellationToken ct = default) =>
            Task.FromResult(Duplicate);
    }


    private static MemoryEntry Observation(string idSuffix, string content) => new()
    {
        Id = $"memories/repo-a/observation/{idSuffix}",
        RepoId = "repo-a",
        Type = MemoryType.Observation,
        Content = content,
        Tags = ["deploy"],
        Provenance = MemoryProvenance.AgentInferred,   // trusted → would boost, but for the canon guard
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = 0.6f,
    };

    private static MemoryEntry DedupEntry(string id, float importance, string content, IEnumerable<string> tags) => new()
    {
        Id = id,
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = content,
        Importance = importance,
        Tags = tags.ToList(),
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
    };
}
