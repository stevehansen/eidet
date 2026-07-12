using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Tests.Services;

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
}
