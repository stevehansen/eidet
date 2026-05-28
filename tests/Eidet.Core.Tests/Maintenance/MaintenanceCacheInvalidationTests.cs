using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Cache-coherence contract for the maintenance pipeline (GitHub issue #14): a maintenance
/// run that mutates entries in a repo must invalidate that repo's recall cache, so a recall
/// cached before the run cannot serve stale data afterward. Driven through the real
/// <see cref="TtlExpiryStage"/> against the shared <see cref="InMemoryEidetStore"/>.
///
/// Note: <see cref="EnrichmentWorker"/> received the analogous post-save invalidation, but it
/// is RavenDB-subscription-coupled (takes an IDocumentStore) and is integration-only — not
/// unit-tested here.
/// </summary>
public class MaintenanceCacheInvalidationTests
{
    [Fact]
    public async Task Maintenance_run_that_expires_an_entry_invalidates_the_recall_cache()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var now = DateTime.UtcNow;

        // Insert directly so we can set ForgetAfter (svc.StoreAsync wouldn't let us).
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/ttl-deploy-1",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "deployment uses argo cd via gitops",
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            ForgetAfter = now.AddDays(-1),
            IsLatest = true,
            Importance = 0.7f,
        });

        // Warm the cache — the entry is still valid (ValidUntil is null) so it surfaces.
        var before = await svc.RecallAsync("repo-a", "deployment");
        Assert.Single(before);

        // Run maintenance: TtlExpiry sets ValidUntil on the past-due entry.
        var orch = new MaintenanceOrchestrator(store, memory: svc);
        var report = await orch.RunAsync(new MaintenanceRequest
        {
            RepoId = "repo-a",
            IsRepoActive = true,
            OnlyStages = new HashSet<string> { TtlExpiryStage.StageName },
        });
        Assert.Equal(1, report.AffectedBy(TtlExpiryStage.StageName));

        // Load-bearing assertion: without invalidation this would serve the stale cached entry.
        var after = await svc.RecallAsync("repo-a", "deployment");
        Assert.Empty(after);
    }

    [Fact]
    public async Task No_op_maintenance_run_leaves_a_warmed_recall_correct()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var now = DateTime.UtcNow;

        // Non-expiring entry (ForgetAfter in the future): TtlExpiry won't touch it.
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/ttl-deploy-keep",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "deployment uses argo cd via gitops",
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            ForgetAfter = now.AddDays(30),
            IsLatest = true,
            Importance = 0.7f,
        });

        var before = await svc.RecallAsync("repo-a", "deployment");
        Assert.Single(before);

        var orch = new MaintenanceOrchestrator(store, memory: svc);
        var report = await orch.RunAsync(new MaintenanceRequest
        {
            RepoId = "repo-a",
            IsRepoActive = true,
            OnlyStages = new HashSet<string> { TtlExpiryStage.StageName },
        });
        Assert.Equal(0, report.AffectedBy(TtlExpiryStage.StageName));

        // Nothing was affected; the entry is still valid and must still surface.
        var after = await svc.RecallAsync("repo-a", "deployment");
        Assert.Single(after);
    }

    [Fact]
    public async Task Maintenance_with_null_memory_completes_and_reports_the_affected_entry()
    {
        var store = new InMemoryEidetStore();
        var now = DateTime.UtcNow;

        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/ttl-deploy-2",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "deployment uses argo cd via gitops",
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            ForgetAfter = now.AddDays(-1),
            IsLatest = true,
            Importance = 0.7f,
        });

        // No memory passed: invalidation is skipped (null-safe) and the run still completes.
        var orch = new MaintenanceOrchestrator(store);
        var report = await orch.RunAsync(new MaintenanceRequest
        {
            RepoId = "repo-a",
            IsRepoActive = true,
            OnlyStages = new HashSet<string> { TtlExpiryStage.StageName },
        });

        Assert.Equal(1, report.AffectedBy(TtlExpiryStage.StageName));
    }
}
