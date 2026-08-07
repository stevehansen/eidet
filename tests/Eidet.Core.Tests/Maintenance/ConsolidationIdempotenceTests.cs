using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Consolidation runs on a schedule (6h by default) over a bucket that reforms identically every
/// cycle, so "emit once" and "emit every cycle forever" look the same on a single run. These pin the
/// second run.
///
/// The field failure this guards: the engine probed for an existing insight with a TYPE-AGNOSTIC
/// duplicate query, and — because an unenriched consolidation emits the representative observation's
/// content verbatim — that probe returned one of the bucket's own observations. The `is it an
/// Insight?` check then failed, the create branch ran, and a fresh verbatim copy was minted on every
/// scheduled run (240 copies of a single observation across two months in a real corpus).
/// </summary>
public class ConsolidationIdempotenceTests
{
    private const string Repo = "P--Test";

    [Fact]
    public async Task Second_run_over_an_unchanged_bucket_creates_nothing()
    {
        var store = new InMemoryEidetStore();
        await SeedBucketAsync(store);

        var first = await BuildEngine(store).ConsolidateAsync(Repo);
        var countAfterFirst = (await store.GetTopScoredAsync(Repo, Enum.GetValues<MemoryType>(), 500)).Count;

        var second = await BuildEngine(store).ConsolidateAsync(Repo);
        var countAfterSecond = (await store.GetTopScoredAsync(Repo, Enum.GetValues<MemoryType>(), 500)).Count;

        Assert.True(first.InsightsCreated > 0, "first run should consolidate the bucket");
        Assert.Equal(0, second.InsightsCreated);
        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact]
    public async Task Repeated_runs_do_not_walk_importance_upward_on_unchanged_evidence()
    {
        var store = new InMemoryEidetStore();
        await SeedBucketAsync(store);

        await BuildEngine(store).ConsolidateAsync(Repo);
        var insight = (await store.GetTopScoredAsync(Repo, [MemoryType.Insight], 50)).Single();
        var importanceAfterFirst = insight.Importance;

        for (var i = 0; i < 5; i++)
            await BuildEngine(store).ConsolidateAsync(Repo);

        var after = (await store.GetTopScoredAsync(Repo, [MemoryType.Insight], 50)).Single();
        Assert.Equal(importanceAfterFirst, after.Importance);
    }

    private static ConsolidationEngine BuildEngine(IEidetStore store) =>
        new(store, null, new MemoryService(store));

    /// <summary>
    /// Three same-tag observations — the minimum bucket — with no functional stage, so the run takes
    /// the Insight branch. Contents differ slightly so grouping is realistic; the emitted insight
    /// still copies the representative verbatim because no enrichment backend is configured, which is
    /// exactly the condition that triggered the loop.
    /// </summary>
    private static async Task SeedBucketAsync(IEidetStore store)
    {
        var now = DateTime.UtcNow.AddDays(-1);
        for (var i = 0; i < 3; i++)
        {
            await store.StoreAsync(new MemoryEntry
            {
                Id = $"memories/{Repo}/observation/seed{i}",
                RepoId = Repo,
                Type = MemoryType.Observation,
                Content = $"the release pipeline signs artifacts before upload, detail {i}",
                Tags = ["release"],
                Provenance = MemoryProvenance.AgentInferred,
                Importance = 0.5f,
                CreatedAt = now,
                Validity = new Validity { ValidFrom = now },
                IsLatest = true,
            });
        }
    }
}
