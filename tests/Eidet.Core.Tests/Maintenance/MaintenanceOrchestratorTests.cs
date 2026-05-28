using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Maintenance;

public class MaintenanceOrchestratorTests
{
    private sealed class StubStage(string name, Func<Task<int>>? run = null, Exception? throws = null) : IMaintenanceStage
    {
        public int InvocationCount { get; private set; }
        public string Name => name;

        public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
        {
            InvocationCount++;
            if (throws != null) throw throws;
            var affected = run != null ? await run() : 0;
            return new StageOutcome(Name, affected);
        }
    }

    private static MaintenanceOrchestrator WithStages(params IMaintenanceStage[] stages)
    {
        IEidetStore store = null!; // stub stages don't touch the store
        return new MaintenanceOrchestrator(store, EnrichmentService.CreateNull(),
            new ConsolidationEngine(store, enrichment: null, memory: new MemoryService(store)), stages);
    }

    [Fact]
    public async Task RunsStagesInGivenOrder()
    {
        var order = new List<string>();
        var a = new StubStage("A", () => { order.Add("A"); return Task.FromResult(1); });
        var b = new StubStage("B", () => { order.Add("B"); return Task.FromResult(2); });
        var c = new StubStage("C", () => { order.Add("C"); return Task.FromResult(3); });

        var orchestrator = WithStages(a, b, c);
        var report = await orchestrator.RunAsync(new MaintenanceRequest { RepoId = "r" });

        Assert.Equal(["A", "B", "C"], order);
        Assert.Equal(3, report.Stages.Count);
        Assert.All(report.Stages, s => Assert.True(s.Succeeded));
    }

    [Fact]
    public async Task SkipStages_SkipsNamedStage()
    {
        var a = new StubStage("A");
        var b = new StubStage("B");
        var c = new StubStage("C");

        var orchestrator = WithStages(a, b, c);
        var report = await orchestrator.RunAsync(new MaintenanceRequest
        {
            RepoId = "r",
            SkipStages = new HashSet<string> { "B" },
        });

        Assert.Equal(1, a.InvocationCount);
        Assert.Equal(0, b.InvocationCount);
        Assert.Equal(1, c.InvocationCount);
        Assert.Equal(2, report.Stages.Count);
        Assert.DoesNotContain(report.Stages, s => s.Name == "B");
    }

    [Fact]
    public async Task OnlyStages_RunsOnlyNamedStages()
    {
        var a = new StubStage("A");
        var b = new StubStage("B");
        var c = new StubStage("C");

        var orchestrator = WithStages(a, b, c);
        var report = await orchestrator.RunAsync(new MaintenanceRequest
        {
            RepoId = "r",
            OnlyStages = new HashSet<string> { "B" },
        });

        Assert.Equal(0, a.InvocationCount);
        Assert.Equal(1, b.InvocationCount);
        Assert.Equal(0, c.InvocationCount);
        Assert.Single(report.Stages);
        Assert.Equal("B", report.Stages[0].Name);
    }

    [Fact]
    public async Task ThrowingStage_DoesNotAbortPipeline()
    {
        var a = new StubStage("A");
        var b = new StubStage("B", throws: new InvalidOperationException("boom"));
        var c = new StubStage("C");

        var orchestrator = WithStages(a, b, c);
        var report = await orchestrator.RunAsync(new MaintenanceRequest { RepoId = "r" });

        Assert.Equal(1, a.InvocationCount);
        Assert.Equal(1, b.InvocationCount);
        Assert.Equal(1, c.InvocationCount);

        var bOutcome = report.Stages.Single(s => s.Name == "B");
        Assert.False(bOutcome.Succeeded);
        Assert.Equal("boom", bOutcome.Error);
        Assert.Contains(report.Failures, f => f.Name == "B");
    }

    [Fact]
    public async Task Cancellation_StopsBeforeNextStage()
    {
        using var cts = new CancellationTokenSource();

        var a = new StubStage("A", () => { cts.Cancel(); return Task.FromResult(0); });
        var b = new StubStage("B");

        var orchestrator = WithStages(a, b);
        var report = await orchestrator.RunAsync(new MaintenanceRequest { RepoId = "r" }, cts.Token);

        Assert.Equal(1, a.InvocationCount);
        Assert.Equal(0, b.InvocationCount);
        Assert.Single(report.Stages);
    }

    [Fact]
    public async Task AffectedBy_ReturnsMatchingStageCount()
    {
        var a = new StubStage("A", () => Task.FromResult(7));
        var orchestrator = WithStages(a);

        var report = await orchestrator.RunAsync(new MaintenanceRequest { RepoId = "r" });
        Assert.Equal(7, report.AffectedBy("A"));
        Assert.Equal(0, report.AffectedBy("DoesNotExist"));
    }

    [Fact]
    public void DefaultStages_IncludesAllNineStages()
    {
        var names = MaintenanceOrchestrator.DefaultStages().Select(s => s.Name).ToList();
        Assert.Equal(9, names.Count);
        Assert.Contains("TtlExpiry", names);
        Assert.Contains("ObservationRetention", names);
        Assert.Contains("DedupSweep", names);
        Assert.Contains("ImportanceDecay", names);
        Assert.Contains("OrphanCleanup", names);
        Assert.Contains("EnrichmentCleanup", names);
        Assert.Contains("HeuristicEnrichmentBackfill", names);
        Assert.Contains("OllamaEnrichment", names);
        Assert.Contains("Consolidation", names);
    }

    [Fact]
    public async Task Report_ContainsRepoIdAndCompletedAt()
    {
        var orchestrator = WithStages(new StubStage("A"));
        var before = DateTime.UtcNow;
        var report = await orchestrator.RunAsync(new MaintenanceRequest { RepoId = "abc" });

        Assert.Equal("abc", report.RepoId);
        Assert.InRange(report.CompletedAt, before, DateTime.UtcNow.AddSeconds(1));
    }
}
