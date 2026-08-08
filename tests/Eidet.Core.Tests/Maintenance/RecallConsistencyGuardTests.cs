using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Tests.Services;
using Eidet.Core.Text;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The structural merge guard (#39): a merge survives only if the survivor still surfaces in the
/// top-k for the discard's own retrieval intent. Driven over the in-memory store's lexical fallback
/// (no embeddings), which is the same WordSimilarity signal the dedup lexical pass uses.
/// </summary>
public class RecallConsistencyGuardTests
{
    private const string Repo = "guard-repo";

    private static MemoryEntry Insight(string id, string content) => new()
    {
        Id = $"memories/{Repo}/insight/{id}",
        RepoId = Repo,
        Type = MemoryType.Insight,
        Content = content,
        Importance = 0.6f,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
    };

    [Fact]
    public async Task Survives_WhenSurvivorSharesTheDiscardsContent()
    {
        var store = new InMemoryEidetStore();
        var survivor = Insight("s", "the deployment pipeline runs database migrations before the app server");
        var discard = Insight("d", "the deployment pipeline runs the database migrations before the app server");
        await store.StoreAsync(survivor);
        await store.StoreAsync(discard);

        Assert.True(await RecallConsistencyGuard.SurvivesAsync(store, Repo, survivor, discard, k: 10));
    }

    [Fact]
    public async Task Rejects_WhenSurvivorDoesNotSurfaceForTheDiscardsIntent()
    {
        var store = new InMemoryEidetStore();
        var survivor = Insight("s", "totally unrelated content about frontend avatar rendering");
        var discard = Insight("d", "the deployment pipeline runs database migrations before the app server");
        await store.StoreAsync(survivor);
        await store.StoreAsync(discard);

        // At k=1 the top hit for the discard's intent is the discard itself; the unrelated survivor is
        // pushed out, so folding the discard into it would lose retrievability → veto.
        Assert.False(await RecallConsistencyGuard.SurvivesAsync(store, Repo, survivor, discard, k: 1));
    }

    [Fact]
    public async Task Survives_WhenPoolIsCrowdedByHighImportanceUnrelatedMemories()
    {
        // Regression: a top-scored (query-ignoring) candidate pool let 200+ high-importance
        // unrelated memories push the survivor out entirely, falsely vetoing every merge.
        // The full-text pool is query-aware, so crowding can't hide a lexically relevant survivor.
        var store = new InMemoryEidetStore();
        var survivor = Insight("s", "deployment pipeline runs database migrations first");
        var discard = Insight("d", "deployment pipeline runs all database migrations first");
        survivor.Importance = 0.3f;
        discard.Importance = 0.3f;
        await store.StoreAsync(survivor);
        await store.StoreAsync(discard);
        for (var i = 0; i < 250; i++)
        {
            var filler = Insight($"f{i}", $"frontend avatar cache node {i:D4}");
            filler.Importance = 0.9f;
            await store.StoreAsync(filler);
        }

        Assert.True(await RecallConsistencyGuard.SurvivesAsync(store, Repo, survivor, discard, k: 10));
    }

    [Fact]
    public async Task Rejects_WhenLiveNearDuplicatesCrowdTheSurvivorOut()
    {
        // 30 live siblings all closer to the discard's intent than the survivor is. They are real
        // memories that the fold does not remove, so the survivor genuinely stops being the answer
        // for this intent and the merge must not proceed. This is the guard doing its job, and it is
        // what stopped a lineage pass from folding same-source memories that were not duplicates.
        var store = new InMemoryEidetStore();
        var survivor = Insight("zz-survivor", "deployment pipeline runs database migrations first");
        var discard = Insight("d", "the deployment pipeline runs all of the database migrations first");
        await store.StoreAsync(survivor);
        await store.StoreAsync(discard);

        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Insight($"copy{i:D2}",
                $"the deployment pipeline runs all of the database migrations first, copy {i}"));

        Assert.False(await RecallConsistencyGuard.SurvivesAsync(store, Repo, survivor, discard, k: 10));
    }

    [Fact]
    public async Task Already_retired_rows_do_not_count_as_competitors()
    {
        // Index lag within a bulk run: rows an earlier fold retired are still ranked, but their
        // documents are already closed, so they are not part of the corpus this fold leaves behind.
        // The vector arm carried no validity re-check (only the lexical fallback did), so on an
        // embeddings-backed store every fold after the first in a bulk run could be vetoed by
        // memories that no longer existed.
        var store = new LaggingIndexStore();
        var survivor = Insight("zz-survivor", "deployment pipeline runs database migrations first");
        var discard = Insight("d", "the deployment pipeline runs all of the database migrations first");
        await store.StoreAsync(survivor);
        await store.StoreAsync(discard);

        for (var i = 0; i < 30; i++)
        {
            var retired = Insight($"copy{i:D2}", $"the deployment pipeline runs all of the database migrations first, copy {i}");
            retired.Validity.ValidUntil = DateTime.UtcNow;
            retired.ForgetReason = "Dedup merged into memories/guard-repo/insight/elsewhere";
            await store.StoreAsync(retired);
        }

        Assert.True(await RecallConsistencyGuard.SurvivesAsync(store, Repo, survivor, discard, k: 10));
    }
}

/// <summary>
/// A store whose search index never notices a retirement — the production condition the merge guard has
/// to survive. RavenDB applies the live-only filter in the index, while a bulk run commits each
/// retirement to the document straight away, so a ranking taken mid-run lists rows whose document is
/// already closed.
///
/// Serving hits from <see cref="VectorSearchAsync"/> also puts the guard on its semantic arm, which
/// <see cref="InMemoryEidetStore"/> leaves untested by returning nothing — every other test in the suite
/// therefore exercised only the lexical fallback. Ranking is the same <see cref="WordSimilarity"/> order
/// the lexical arm uses; the fetch is deliberately wide so a truncated page can never be mistaken for a
/// crowded one.
/// </summary>
internal sealed class LaggingIndexStore : InMemoryEidetStore
{
    public override async Task<List<MemoryEntry>> VectorSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        var stale = await base.FullTextSearchAsync(
            repoIds, new MemoryQuery { Text = query.Text, Limit = 10_000, IncludeExpired = true }, ct);

        return stale
            .OrderByDescending(e => WordSimilarity.Compute(query.Text, e.Content))
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Take(query.Limit)
            .ToList();
    }
}
