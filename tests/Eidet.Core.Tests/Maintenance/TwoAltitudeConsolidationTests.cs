using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Two-altitude procedure emission (#39): a stage-tagged (procedure-shaped) observation cluster emits a
/// fine-grained steps Procedure + a script-like abstraction linked to it; an all-None cluster falls
/// through to today's single-altitude Insight path unchanged (the #38 field contract).
/// </summary>
public class TwoAltitudeConsolidationTests
{
    private const string Repo = "consol-repo";

    private static MemoryEntry Obs(string id, string content, FunctionalStage stage) => new()
    {
        Id = $"memories/{Repo}/observation/{id}",
        RepoId = Repo,
        Type = MemoryType.Observation,
        Stage = stage,
        Content = content,
        Tags = ["parser", "refactor"],
        Importance = 0.6f,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
    };

    [Fact]
    public async Task StagedCluster_EmitsTwoLinkedProcedures()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Obs("s1", "locate the failing assertion in the parser test suite", FunctionalStage.Edit));
        await store.StoreAsync(Obs("s2", "edit the tokenizer to handle the trailing comma case", FunctionalStage.Edit));
        await store.StoreAsync(Obs("s3", "rerun the parser tests to confirm the edit holds", FunctionalStage.Edit));

        var result = await new ConsolidationEngine(store, null, new MemoryService(store)).ConsolidateAsync(Repo);

        Assert.Equal(2, result.ProceduresCreated);
        Assert.Equal(0, result.InsightsCreated);

        var procs = await store.BrowseAsync(Repo, 0, 100, MemoryType.Procedure);
        Assert.Equal(2, procs.Count);
        Assert.All(procs, p => Assert.Equal(FunctionalStage.Edit, p.Stage));

        // The abstraction links down to the fine-grained steps procedure.
        var abstraction = procs.Single(p => p.Links.Any(l => l.Relation == "abstracts"));
        var fine = procs.Single(p => p.Links.Count == 0);
        Assert.Contains(fine.Id, abstraction.DerivedFrom);
        Assert.Equal(fine.Id, abstraction.Links.Single(l => l.Relation == "abstracts").TargetMemoryId);
    }

    [Fact]
    public async Task UnstagedCluster_FallsBackToSingleInsight()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Obs("n1", "the parser reads tokens from a shared buffer", FunctionalStage.None));
        await store.StoreAsync(Obs("n2", "the parser buffer is refilled lazily on demand", FunctionalStage.None));
        await store.StoreAsync(Obs("n3", "the parser buffer size is tuned per document type", FunctionalStage.None));

        var result = await new ConsolidationEngine(store, null, new MemoryService(store)).ConsolidateAsync(Repo);

        Assert.Equal(0, result.ProceduresCreated);
        Assert.Equal(1, result.InsightsCreated);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 100, MemoryType.Procedure));
        var insight = Assert.Single(await store.BrowseAsync(Repo, 0, 100, MemoryType.Insight));
        Assert.Equal(FunctionalStage.None, insight.Stage);
    }
}
