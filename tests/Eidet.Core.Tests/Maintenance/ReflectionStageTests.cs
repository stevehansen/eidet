using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Drives the real <see cref="ReflectionStage"/> through <see cref="MaintenanceOrchestrator"/>
/// (OnlyStages, like <see cref="DriftReviewStageTests"/>). The feature SHIPS DORMANT: the stage must
/// no-op unless reflection is explicitly enabled, an enrichment backend is reachable, AND the repo is
/// active — so the default nightly run never mints from reflection even with a live model and residue.
/// </summary>
public class ReflectionStageTests
{
    private const string Repo = "repo-a";

    private const string OneInsightJson =
        """[{"content":"Redis connection pooling stays stable under sustained production load across restarts","type":"insight","valence":"neutral","tags":["redis"]}]""";

    private static MemoryEntry Echoed(string idSuffix) => new()
    {
        Id = $"memories/{Repo}/observation/{idSuffix}",
        RepoId = Repo,
        Type = MemoryType.Observation,
        Content = $"observation {idSuffix} recorded redis pooling behavior under production load",
        Provenance = MemoryProvenance.AgentInferred,
        EchoCount = 5,
        Importance = 0.6f,
        CreatedAt = DateTime.UtcNow.AddDays(-10),
        LastAccessedAt = DateTime.UtcNow.AddDays(-10),
        Validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-10) },
        IsLatest = true,
    };

    private static EnrichmentService Enrichment(string? reflectResponse, bool available = true) =>
        new(new InMemoryEnrichmentAdapter { IsAvailable = available }.SetResponse(EnrichmentPrompt.Reflect, reflectResponse));

    private static Task<MaintenanceReport> RunReflectionStageAsync(
        InMemoryEidetStore store, EnrichmentService enrichment, ReflectionConfig cfg, bool repoActive = true)
    {
        var orch = new MaintenanceOrchestrator(store, new MemoryService(store), enrichment, reflectionConfig: cfg);
        return orch.RunAsync(new MaintenanceRequest
        {
            RepoId = Repo,
            IsRepoActive = repoActive,
            OnlyStages = new HashSet<MaintenanceStep> { MaintenanceStep.Reflection },
        });
    }

    [Fact]
    public async Task DisabledConfig_MintsNothing_EvenWhenLlmAvailableWithResidue()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));       // residue is present
        using var enrichment = Enrichment(OneInsightJson); // model is available and would propose
        Assert.True(enrichment.IsAvailable);

        var report = await RunReflectionStageAsync(store, enrichment, new ReflectionConfig { Enabled = false });

        Assert.Equal(0, report.AffectedBy(ReflectionStage.StageName));
        Assert.Empty(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
    }

    [Fact]
    public async Task InactiveRepo_MintsNothing_EvenWhenEnabledAndAvailable()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));
        using var enrichment = Enrichment(OneInsightJson);

        var report = await RunReflectionStageAsync(
            store, enrichment, new ReflectionConfig { Enabled = true }, repoActive: false);

        Assert.Equal(0, report.AffectedBy(ReflectionStage.StageName));
        Assert.Empty(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
    }

    [Fact]
    public async Task EnabledConfig_WithResidueAndLlm_MintsAndReportsAffected()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));
        using var enrichment = Enrichment(OneInsightJson);

        var report = await RunReflectionStageAsync(store, enrichment, new ReflectionConfig { Enabled = true });

        Assert.Equal(1, report.AffectedBy(ReflectionStage.StageName));
        Assert.Single(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
    }
}
