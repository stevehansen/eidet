using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Benchmark.Tests;

/// <summary>
/// FAMA (forget-and-modify-aware) behavioral guard for the AMA-Bench <c>StateUpdating</c>
/// capability: once a memory is superseded or forgotten, the stale version must never resurface in
/// recall or wake-up context — including across a cross-repo fan-out — while a live sibling still
/// does. Driven through the real <see cref="MemoryService"/> over <see cref="BenchInMemoryStore"/>.
/// The boolean outcome feeds the scorecard's StateUpdating row (see <see cref="StateUpdatingPasses"/>).
/// </summary>
public class FamaForgetTests
{
    private const string RepoA = "fama-repo-a";
    private const string RepoB = "fama-repo-b";

    private static (MemoryService Svc, BenchInMemoryStore Store) NewService()
    {
        var store = new BenchInMemoryStore();
        var layers = new LayerService(store);
        return (new MemoryService(store, layers), store);
    }

    /// <summary>Asserts the store succeeded and returns its (now non-null) id.</summary>
    private static string Id(StoreResult result)
    {
        Assert.True(result.Success, result.Reason);
        Assert.NotNull(result.Id);
        return result.Id;
    }

    [Fact]
    public async Task Supersede_RecallAndContext_ReturnTheLiveVersionNeverTheStale()
    {
        var (svc, _) = NewService();

        var originalId = Id(await svc.StoreAsync(
            RepoA, "Recall fuses lexical and vector arms with min-max normalization", MemoryType.Insight));

        // Supersede via a content edit — the EditAsync path flips IsLatest=false on the original
        // and stores a fresh live entry.
        var edited = await svc.UpdateMemoryAsync(
            originalId, content: "Recall fuses lexical and vector arms plus a UCB exploration bonus");
        Assert.True(edited);

        var recalled = await svc.RecallAsync(RepoA, "recall fuses arms");
        Assert.DoesNotContain(recalled, r => r.Id == originalId);          // stale original gone
        Assert.Contains(recalled, r => r.Content.Contains("UCB"));         // live version present

        var context = await svc.GetContextAsync(RepoA);
        Assert.DoesNotContain(originalId, context);
    }

    [Fact]
    public async Task Forget_WithReason_NeverSurfaces_WhileLiveSiblingStillDoes()
    {
        var (svc, _) = NewService();

        var doomedId = Id(await svc.StoreAsync(
            RepoA, "Embedded RavenDB is the zero-setup default storage mode", MemoryType.Insight));
        var siblingId = Id(await svc.StoreAsync(
            RepoA, "External RavenDB mode targets an existing cluster install", MemoryType.Insight));

        var forgotten = await svc.ForgetAsync(doomedId, reason: "Storage mode was consolidated");
        Assert.True(forgotten);

        var recalled = await svc.RecallAsync(RepoA, "ravendb storage mode");
        Assert.DoesNotContain(recalled, r => r.Id == doomedId);
        Assert.Contains(recalled, r => r.Id == siblingId);

        var context = await svc.GetContextAsync(RepoA);
        Assert.DoesNotContain(doomedId, context);
    }

    [Fact]
    public async Task CrossRepo_StaleSuppression_HoldsAcrossTheMountedLayerUnion()
    {
        var (svc, store) = NewService();

        // Mount a shared layer so a recall in RepoA fans out to RepoB.
        await store.StoreMountedLayerAsync(new MemoryLayer
        {
            Id = "layers/shared/fama",
            Name = "fama-shared",
            Type = LayerType.Shared,
            ApplicableRepos = [RepoA],
            ApplicablePackages = [],
        });
        // The layer also resolves when querying RepoA's mounted layers; it adds RepoB to the union.
        var layer = await store.GetLayerAsync("layers/shared/fama");
        layer!.ApplicableRepos = [RepoA, RepoB];
        await store.StoreMountedLayerAsync(layer);

        var inBId = Id(await svc.StoreAsync(
            RepoB, "Cross-repo recall fans out across mounted shared layers", MemoryType.Insight));

        // Sanity: the cross-repo recall reaches RepoB's entry from RepoA.
        var before = await svc.RecallAsync(RepoA, new RecallOptions("cross-repo recall fans out") { CrossRepo = true });
        Assert.Contains(before, r => r.Id == inBId);

        // Forget it; the cross-repo recall must no longer surface it.
        Assert.True(await svc.ForgetAsync(inBId, reason: "No longer relevant cross-repo"));
        var after = await svc.RecallAsync(RepoA, new RecallOptions("cross-repo recall fans out") { CrossRepo = true });
        Assert.DoesNotContain(after, r => r.Id == inBId);
    }

    /// <summary>
    /// The StateUpdating pass-rate the scorecard reports: runs the three guards and returns true only
    /// if every one holds. Kept as a static so the scorecard build (<see cref="BenchmarkScorecardTests"/>)
    /// can fold it into the StateUpdating capability line without duplicating the scenarios.
    /// </summary>
    public static async Task<bool> StateUpdatingPasses()
    {
        var t = new FamaForgetTests();
        try
        {
            await t.Supersede_RecallAndContext_ReturnTheLiveVersionNeverTheStale();
            await t.Forget_WithReason_NeverSurfaces_WhileLiveSiblingStillDoes();
            await t.CrossRepo_StaleSuppression_HoldsAcrossTheMountedLayerUnion();
            return true;
        }
        catch (Xunit.Sdk.XunitException)
        {
            // Only assertion failures map to "capability does not hold" → false. A non-assertion
            // exception (NRE, store fault) is a genuine bug and is deliberately left to propagate
            // and fail the build, rather than being silently scored as a clean fail.
            return false;
        }
    }
}
