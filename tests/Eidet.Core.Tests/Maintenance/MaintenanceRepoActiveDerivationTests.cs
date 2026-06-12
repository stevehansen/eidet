using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Issue #22's footgun fix: the orchestrator is the single derivation site for IsRepoActive.
/// A null request value is derived from MemoryService.IsRepoActive; an explicit value overrides
/// the derivation. The string RunAsync overload normalizes the repo path and still derives.
///
/// Activity is tracked in-process by MemoryService (RepoActivityTracker), populated only when a
/// repo is touched THROUGH that same service instance — so "active" fixtures call svc.StoreAsync
/// (which Tracks the normalized id), and "inactive" repos are simply never touched through svc.
/// </summary>
public class MaintenanceRepoActiveDerivationTests
{
    /// <summary>Captures the IsRepoActive the orchestrator built into the per-run context.</summary>
    private sealed class CaptureStage : IMaintenanceStage
    {
        public string Name => "Capture";
        public bool? Captured { get; private set; }

        public Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
        {
            Captured = ctx.IsRepoActive;
            return Task.FromResult(new StageOutcome(Name, 0));
        }
    }

    private static MaintenanceOrchestrator OrchestratorWith(
        IEidetStore store, MemoryService svc, params IMaintenanceStage[] stages) =>
        new(store, svc, EnrichmentService.CreateNull(),
            new ConsolidationEngine(store, enrichment: null, memory: svc), stages);

    // ─── Derivation from MemoryService activity (request value null) ─────────

    [Fact]
    public async Task NullIsRepoActive_ActiveRepo_DerivesTrue()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        // Touch the repo through svc → RepoActivityTracker records it as active.
        await svc.StoreAsync("repo-a", "deployment uses argo cd via gitops", MemoryType.Insight);

        var capture = new CaptureStage();
        var orch = OrchestratorWith(store, svc, capture);
        await orch.RunAsync(new MaintenanceRequest { RepoId = "repo-a", IsRepoActive = null });

        Assert.True(capture.Captured);
    }

    [Fact]
    public async Task NullIsRepoActive_InactiveRepo_DerivesFalse()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        // Entry exists in the store but was inserted directly — svc never Tracked this repo.
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-cold/insight/x",
            RepoId = "repo-cold",
            Type = MemoryType.Insight,
            Content = "deployment uses argo cd via gitops",
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        });

        var capture = new CaptureStage();
        var orch = OrchestratorWith(store, svc, capture);
        await orch.RunAsync(new MaintenanceRequest { RepoId = "repo-cold", IsRepoActive = null });

        Assert.False(capture.Captured);
    }

    // ─── Explicit override honored (no derivation) ───────────────────────────

    [Fact]
    public async Task ExplicitTrue_InactiveRepo_OverridesDerivation()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store); // repo never touched → would derive false
        var capture = new CaptureStage();
        var orch = OrchestratorWith(store, svc, capture);

        await orch.RunAsync(new MaintenanceRequest { RepoId = "repo-cold", IsRepoActive = true });

        Assert.True(capture.Captured);
    }

    // ─── String overload: normalizes path AND derives (not left default) ─────

    [Fact]
    public async Task RunAsyncStringOverload_NormalizesRepoIdAndDerivesActive()
    {
        const string path = @"C:\Projects\My_Repo\";
        var normalized = RepoIdNormalizer.Normalize(path);

        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        // Make the NORMALIZED repo active by storing through svc with the same path.
        await svc.StoreAsync(path, "deployment uses argo cd via gitops", MemoryType.Insight);

        var capture = new CaptureStage();
        var orch = OrchestratorWith(store, svc, capture);
        var report = await orch.RunAsync(path);

        Assert.Equal(normalized, report.RepoId);
        // Derived (not left null/default): an active repo yields true via the string path too.
        Assert.True(capture.Captured);
    }

    [Fact]
    public async Task RunAsyncStringOverload_InactiveRepo_DerivesFalse()
    {
        const string path = @"C:\Projects\Cold_Repo\";
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store); // never touched through svc
        var capture = new CaptureStage();
        var orch = OrchestratorWith(store, svc, capture);

        var report = await orch.RunAsync(path);

        Assert.Equal(RepoIdNormalizer.Normalize(path), report.RepoId);
        Assert.False(capture.Captured); // derived, not a stray true
    }

    // ─── AffectedBy(MaintenanceStep) delegates to AffectedBy(string) ─────────

    [Fact]
    public async Task AffectedBy_EnumOverload_MatchesStringOverload()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        // Past-due TTL entry so the real TtlExpiry stage reports a non-zero, comparable count.
        var now = DateTime.UtcNow;
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/ttl",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "deployment uses argo cd via gitops",
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            ForgetAfter = now.AddDays(-1),
            IsLatest = true,
        });

        var orch = new MaintenanceOrchestrator(store, svc);
        var report = await orch.RunAsync(new MaintenanceRequest
        {
            RepoId = "repo-a",
            IsRepoActive = true,
            OnlyStages = new HashSet<MaintenanceStep> { MaintenanceStep.TtlExpiry },
        });

        Assert.Equal(1, report.AffectedBy(MaintenanceStep.TtlExpiry));
        Assert.Equal(report.AffectedBy("TtlExpiry"), report.AffectedBy(MaintenanceStep.TtlExpiry));
    }
}
