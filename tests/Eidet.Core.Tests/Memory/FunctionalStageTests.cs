using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Functional-stage hard pre-filter (#38): the None-as-wildcard recall semantics, the recall-cache key
/// distinctness (incl. the Valence-omission fix found while designing), and the write-path threading.
/// </summary>
public class FunctionalStageTests
{
    private const string Repo = "stage-repo";
    private const string Query = "refactoring the parser module";

    private static async Task<MemoryService> SeededServiceAsync()
    {
        var svc = new MemoryService(new InMemoryEidetStore());
        await svc.StoreAsync(new StoreOptions(Repo, "edit path notes on refactoring the parser module", MemoryType.Insight) { Stage = FunctionalStage.Edit });
        await svc.StoreAsync(new StoreOptions(Repo, "test path notes on refactoring the parser module", MemoryType.Insight) { Stage = FunctionalStage.Test });
        await svc.StoreAsync(new StoreOptions(Repo, "general notes on refactoring the parser module", MemoryType.Insight) { Stage = FunctionalStage.None });
        return svc;
    }

    [Fact]
    public async Task StageFilter_ReturnsRequestedStagePlusNone_ExcludesOtherStages()
    {
        var svc = await SeededServiceAsync();

        var edit = await svc.RecallAsync(Repo, new RecallOptions(Query) { Stage = FunctionalStage.Edit, CrossRepo = false });

        Assert.Contains(edit, r => r.Stage == FunctionalStage.Edit);   // requested stage
        Assert.Contains(edit, r => r.Stage == FunctionalStage.None);   // stage-agnostic wildcard
        Assert.DoesNotContain(edit, r => r.Stage == FunctionalStage.Test); // different stage excluded
    }

    [Fact]
    public async Task NoStageFilter_ReturnsEveryStage()
    {
        var svc = await SeededServiceAsync();

        var all = await svc.RecallAsync(Repo, new RecallOptions(Query) { CrossRepo = false });

        Assert.Contains(all, r => r.Stage == FunctionalStage.Edit);
        Assert.Contains(all, r => r.Stage == FunctionalStage.Test);
        Assert.Contains(all, r => r.Stage == FunctionalStage.None);
    }

    [Fact]
    public async Task StageFilter_OnUntaggedCorpus_IsGracefulNotBlackout()
    {
        // The day-one upgrade case: every memory is None. A strict equality filter would return empty;
        // the wildcard admits them all.
        var svc = new MemoryService(new InMemoryEidetStore());
        await svc.StoreAsync(new StoreOptions(Repo, "legacy note on refactoring the parser module", MemoryType.Insight));

        var edit = await svc.RecallAsync(Repo, new RecallOptions(Query) { Stage = FunctionalStage.Edit, CrossRepo = false });

        Assert.Single(edit);
        Assert.Equal(FunctionalStage.None, edit[0].Stage);
    }

    [Fact]
    public async Task StageFilteredRecall_DoesNotServeCachedUnfilteredResult()
    {
        // Warms the cache under the no-stage key, then a stage-filtered recall must produce its own
        // (smaller) result rather than colliding with the cached one — the ComputeKey Stage term.
        var svc = await SeededServiceAsync();

        var unfiltered = await svc.RecallAsync(Repo, new RecallOptions(Query) { CrossRepo = false });
        Assert.Equal(3, unfiltered.Count);

        var editOnly = await svc.RecallAsync(Repo, new RecallOptions(Query) { Stage = FunctionalStage.Edit, CrossRepo = false });
        Assert.Equal(2, editOnly.Count); // Edit + None, never the cached 3
        Assert.DoesNotContain(editOnly, r => r.Stage == FunctionalStage.Test);
    }

    [Fact]
    public void ComputeKey_IsDistinctForStageAndValence()
    {
        var plain = new MemoryQuery { Text = "auth" };
        var staged = new MemoryQuery { Text = "auth", Stage = FunctionalStage.Edit };
        var valenced = new MemoryQuery { Text = "auth", Valence = Valence.Refuting };

        var kPlain = RecallCache.ComputeKey(Repo, plain, 0.5);
        var kStaged = RecallCache.ComputeKey(Repo, staged, 0.5);
        var kValenced = RecallCache.ComputeKey(Repo, valenced, 0.5);

        Assert.NotEqual(kPlain, kStaged);                       // Stage term
        Assert.NotEqual(kPlain, kValenced);                     // Valence term (the found-bug fix)
        Assert.NotEqual(kStaged, kValenced);
    }

    [Fact]
    public void BuildEntry_ThreadsStageFromOptions()
    {
        var built = WriteValidator.BuildEntry(
            new StoreOptions(Repo, "a specific fact about the test harness setup", MemoryType.Procedure) { Stage = FunctionalStage.Test });

        Assert.True(built.IsBuilt);
        Assert.Equal(FunctionalStage.Test, built.Entry!.Stage);
    }

    [Fact]
    public void BuildEditEntry_CarriesStageForward_UnlessOverridden()
    {
        var original = WriteValidator.BuildEntry(
            new StoreOptions(Repo, "original content about the debug workflow steps", MemoryType.Procedure) { Stage = FunctionalStage.Debug }).Entry!;

        var carried = WriteValidator.BuildEditEntry(original, new EditOptions { Content = "revised content about the debug workflow steps" });
        Assert.Equal(FunctionalStage.Debug, carried.Entry!.Stage);      // carried forward

        var overridden = WriteValidator.BuildEditEntry(original, new EditOptions { Content = "now this is a deploy step instead", Stage = FunctionalStage.Deploy });
        Assert.Equal(FunctionalStage.Deploy, overridden.Entry!.Stage);  // edit override wins
    }

    [Fact]
    public async Task Edit_MetadataOnly_RetagsStageInPlace()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var stored = await svc.StoreAsync(new StoreOptions(Repo, "a locate-step fact about the parser module", MemoryType.Procedure) { Stage = FunctionalStage.Locate });

        Assert.Equal(EditOutcome.Updated, await svc.EditAsync(stored.Id!, new EditOptions { Stage = FunctionalStage.Analyze }));

        Assert.Equal(FunctionalStage.Analyze, (await store.GetAsync(stored.Id!))!.Stage);
    }
}
