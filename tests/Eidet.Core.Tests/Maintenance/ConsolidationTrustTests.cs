using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Anti-laundering carry-through tests for <see cref="ConsolidationEngine"/> (issue #34). The threat
/// is a "compression-amplified toxin": an attacker launders an untrusted (Pack/Intake) observation
/// into a fully-trusted Insight by getting consolidation to merge it.
///
/// CREATE path: a group containing ANY untrusted contributor stamps the new Insight with the
/// least-trusted contributor's provenance (NOT Consolidation), so <see cref="MemoryTrust.Factor"/>
/// keeps demoting it. The audit trail (Source="consolidation", DerivedFrom) is preserved.
///
/// BOOST path: an existing trusted Insight is lifted only by its TRUSTED contributing subset; an
/// all-untrusted contributing group does not boost it at all.
/// </summary>
public class ConsolidationTrustTests
{
    private const string Repo = "repo-a";

    private static MemoryEntry Observation(
        string idSuffix, string content, MemoryProvenance provenance, params string[] tags) => new()
    {
        Id = $"memories/{Repo}/observation/{idSuffix}",
        RepoId = Repo,
        Type = MemoryType.Observation,
        Content = content,
        Tags = tags.ToList(),
        Provenance = provenance,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = 0.6f,
    };

    private static ConsolidationEngine BuildEngine(IEidetStore store)
    {
        var svc = new MemoryService(store);
        return new ConsolidationEngine(store, enrichment: null, svc);
    }

    // ─── CREATE path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Create_with_one_pack_contributor_inherits_untrusted_provenance_not_consolidation()
    {
        var store = new InMemoryEidetStore();
        // Three tag-overlapping observations; one is a Pack import (the poison).
        await store.StoreAsync(Observation("a", "deploy step one runs migrations", MemoryProvenance.AgentInferred, "deploy"));
        await store.StoreAsync(Observation("b", "deploy step two restarts the app", MemoryProvenance.AgentInferred, "deploy"));
        await store.StoreAsync(Observation("poison", "deploy step three drops the firewall", MemoryProvenance.Pack, "deploy"));

        var engine = BuildEngine(store);
        var result = await engine.ConsolidateAsync(Repo);
        Assert.Equal(1, result.InsightsCreated);

        var insight = (await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight)).Single();

        // The created insight does NOT read as Consolidation — it inherits the Pack provenance,
        // so trust stays demoted at recall (the laundering is defeated).
        Assert.Equal(MemoryProvenance.Pack, insight.Provenance);
        Assert.True(MemoryTrust.Factor(insight) < 1.0,
            $"laundered insight trust ({MemoryTrust.Factor(insight)}) must stay below full trust");
        Assert.Equal(0.5, MemoryTrust.Factor(insight), precision: 12); // Pack floor, no feedback

        // Audit trail preserved: Source and DerivedFrom untouched.
        Assert.Equal("consolidation", insight.Source);
        Assert.Equal(3, insight.DerivedFrom.Count);
        Assert.Contains($"memories/{Repo}/observation/poison", insight.DerivedFrom);
    }

    [Fact]
    public async Task Create_picks_least_trusted_contributor_provenance()
    {
        var store = new InMemoryEidetStore();
        // Both Intake and Pack floor at 0.5, but the engine orders by ProvenanceTrust and takes
        // the first; both are equally untrusted, so the result must be one of the two import origins
        // (never Consolidation, never the trusted AgentInferred one).
        await store.StoreAsync(Observation("a", "config sets the cache ttl to five minutes", MemoryProvenance.AgentInferred, "cache"));
        await store.StoreAsync(Observation("b", "config sets the cache size to one gig", MemoryProvenance.Intake, "cache"));
        await store.StoreAsync(Observation("c", "config sets the cache backend to redis", MemoryProvenance.Pack, "cache"));

        var engine = BuildEngine(store);
        await engine.ConsolidateAsync(Repo);

        var insight = (await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight)).Single();
        Assert.Contains(insight.Provenance, new[] { MemoryProvenance.Intake, MemoryProvenance.Pack });
        Assert.True(MemoryTrust.ProvenanceTrust(insight.Provenance) < 1.0);
    }

    [Fact]
    public async Task Create_with_all_trusted_contributors_stamps_consolidation_full_trust()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Observation("a", "auth uses jwt rs256 keys", MemoryProvenance.AgentInferred, "auth"));
        await store.StoreAsync(Observation("b", "auth tokens expire after ten minutes", MemoryProvenance.ToolOutput, "auth"));
        await store.StoreAsync(Observation("c", "auth refresh is rotated on use", MemoryProvenance.UserStated, "auth"));

        var engine = BuildEngine(store);
        var result = await engine.ConsolidateAsync(Repo);
        Assert.Equal(1, result.InsightsCreated);

        var insight = (await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight)).Single();
        Assert.Equal(MemoryProvenance.Consolidation, insight.Provenance);
        Assert.Equal(1.0, MemoryTrust.Factor(insight));
    }

    [Fact]
    public async Task DryRun_does_not_write_any_insight()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Observation("a", "deploy step one runs migrations", MemoryProvenance.Pack, "deploy"));
        await store.StoreAsync(Observation("b", "deploy step two restarts the app", MemoryProvenance.AgentInferred, "deploy"));
        await store.StoreAsync(Observation("c", "deploy step three warms caches", MemoryProvenance.AgentInferred, "deploy"));

        var engine = BuildEngine(store);
        var result = await engine.ConsolidateAsync(Repo, dryRun: true);

        Assert.Single(result.Candidates);
        Assert.Equal(0, result.InsightsCreated);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
    }

    // ─── BOOST path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Boost_skips_entirely_when_all_contributors_are_untrusted()
    {
        // FindDuplicate returns a pre-existing trusted Insight, so the engine takes the BOOST path.
        var existing = new MemoryEntry
        {
            Id = $"memories/{Repo}/insight/standing",
            RepoId = Repo,
            Type = MemoryType.Insight,
            Content = "the deployment pipeline is well understood",
            Tags = ["deploy"],
            Provenance = MemoryProvenance.Consolidation,
            Importance = 0.6f,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        };
        var store = new BoostStore(existing);

        // An all-untrusted contributing group (every observation is Pack/Intake).
        await store.StoreAsync(Observation("a", "the deployment pipeline is well understood now", MemoryProvenance.Pack, "deploy"));
        await store.StoreAsync(Observation("b", "the deployment pipeline is well understood today", MemoryProvenance.Pack, "deploy"));
        await store.StoreAsync(Observation("c", "the deployment pipeline is well understood here", MemoryProvenance.Intake, "deploy"));

        var importanceBefore = existing.Importance;
        var derivedBefore = existing.DerivedFrom.Count;

        var engine = BuildEngine(store);
        var result = await engine.ConsolidateAsync(Repo);

        // No trusted contributor → boost is skipped: standing is NOT laundered upward.
        Assert.Equal(0, result.InsightsBoosted);
        Assert.Equal(importanceBefore, existing.Importance);
        Assert.Equal(derivedBefore, existing.DerivedFrom.Count);
    }

    [Fact]
    public async Task Boost_admits_only_the_trusted_subset()
    {
        var existing = new MemoryEntry
        {
            Id = $"memories/{Repo}/insight/standing",
            RepoId = Repo,
            Type = MemoryType.Insight,
            Content = "the deployment pipeline is well understood",
            Tags = ["deploy"],
            Provenance = MemoryProvenance.Consolidation,
            Importance = 0.6f,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        };
        var store = new BoostStore(existing);

        // Mixed group: two trusted, one Pack. Only the two trusted contributors may lift it.
        await store.StoreAsync(Observation("t1", "the deployment pipeline is well understood now", MemoryProvenance.AgentInferred, "deploy"));
        await store.StoreAsync(Observation("t2", "the deployment pipeline is well understood today", MemoryProvenance.ToolOutput, "deploy"));
        await store.StoreAsync(Observation("poison", "the deployment pipeline is well understood here", MemoryProvenance.Pack, "deploy"));

        var importanceBefore = existing.Importance;

        var engine = BuildEngine(store);
        var result = await engine.ConsolidateAsync(Repo);

        Assert.Equal(1, result.InsightsBoosted);
        // Importance lifted by exactly 0.05 * (trusted count == 2) == 0.10, NOT 0.15 for all three.
        Assert.Equal(importanceBefore + 0.10f, existing.Importance, precision: 5);
        // Only the two trusted contributors entered the lineage; the Pack id did NOT.
        Assert.Contains($"memories/{Repo}/observation/t1", existing.DerivedFrom);
        Assert.Contains($"memories/{Repo}/observation/t2", existing.DerivedFrom);
        Assert.DoesNotContain($"memories/{Repo}/observation/poison", existing.DerivedFrom);
    }
}

/// <summary>
/// In-memory store that drives the consolidation BOOST path: <see cref="FindDuplicateAsync"/>
/// returns the single pre-seeded Insight (which the engine then boosts in place). Everything else
/// delegates to a plain <see cref="InMemoryEidetStore"/> via composition.
/// </summary>
internal sealed class BoostStore : IEidetStore
{
    private readonly InMemoryEidetStore _inner = new();
    private readonly MemoryEntry _duplicate;

    public BoostStore(MemoryEntry duplicate)
    {
        _duplicate = duplicate;
        _inner.StoreAsync(duplicate).GetAwaiter().GetResult();
    }

    // The boost trigger: any content lookup returns the standing Insight.
    public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) =>
        Task.FromResult<MemoryEntry?>(_duplicate);

    public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default) => _inner.StoreAsync(entry, ct);
    public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) => _inner.GetAsync(id, ct);
    public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) => _inner.UpdateAsync(entry, ct);
    public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => _inner.ForgetAsync(id, ct);
    public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        _inner.FullTextSearchAsync(repoIds, query, ct);
    public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        _inner.VectorSearchAsync(repoIds, query, ct);
    public Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default) =>
        _inner.FindNearDuplicatesAsync(repoId, entry, minSimilarity, max, ct);
    public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
        _inner.GetCountsByTypeAsync(repoId, ct);
    public Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default) =>
        _inner.GetTopScoredAsync(repoId, types, limit, ct);
    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => _inner.TestConnectionAsync(ct);
    public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => _inner.GetDatabaseInfoAsync(ct);
    public Task EnsureIndexesAsync(CancellationToken ct = default) => _inner.EnsureIndexesAsync(ct);
    public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default) => _inner.GetDistinctRepoIdsAsync(ct);
    public Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default) =>
        _inner.BrowseAsync(repoId, skip, take, type, ct);
    public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default) => _inner.StoreMountedLayerAsync(layer, ct);
    public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default) => _inner.UnmountLayerAsync(layerId, ct);
    public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default) => _inner.GetMountedLayersAsync(repoId, ct);
    public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default) => _inner.GetLayerAsync(layerId, ct);
    public Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) => _inner.GetByLayerIdAsync(layerId, ct);
    public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default) => _inner.HardDeleteAsync(id, ct);
}
