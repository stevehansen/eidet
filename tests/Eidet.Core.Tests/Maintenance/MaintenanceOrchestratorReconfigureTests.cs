using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The pipeline captures its drift-review and reflection settings at construction; a live config
/// reload must reach the NEXT pass through <see cref="MaintenanceOrchestrator.Reconfigure"/>.
/// Before it existed, the reload endpoint reported success while the night kept the old values.
/// </summary>
public class MaintenanceOrchestratorReconfigureTests
{
    private sealed class CaptureStage : IMaintenanceStage
    {
        public string Name => "Capture";
        public DriftReviewConfig? Drift { get; private set; }
        public ReflectionConfig? Reflection { get; private set; }

        public Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
        {
            Drift = ctx.Drift;
            Reflection = ctx.Reflection.Config;
            return Task.FromResult(new StageOutcome(Name, 0));
        }
    }

    [Fact]
    public async Task Reconfigure_NextPassSeesFreshDriftAndReflectionSettings()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var capture = new CaptureStage();
        var orch = new MaintenanceOrchestrator(store, svc, EnrichmentService.CreateNull(),
            new ConsolidationEngine(store, enrichment: null, memory: svc), [capture],
            drift: new DriftReviewConfig { Enabled = false, NightlyBatch = 25 },
            reflectionConfig: new ReflectionConfig { Enabled = false });

        await orch.RunAsync(new MaintenanceRequest { RepoId = "repo-a", IsRepoActive = true });
        Assert.False(capture.Drift!.Enabled);
        Assert.False(capture.Reflection!.Enabled);

        orch.Reconfigure(
            new DriftReviewConfig { Enabled = true, NightlyBatch = 10 },
            new ReflectionConfig { Enabled = true });

        await orch.RunAsync(new MaintenanceRequest { RepoId = "repo-a", IsRepoActive = true });
        Assert.True(capture.Drift!.Enabled);
        Assert.Equal(10, capture.Drift.NightlyBatch);
        Assert.True(capture.Reflection!.Enabled);
    }
}
