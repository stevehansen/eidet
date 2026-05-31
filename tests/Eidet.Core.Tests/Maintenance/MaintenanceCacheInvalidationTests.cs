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
        var orch = new MaintenanceOrchestrator(store, svc);
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

        var orch = new MaintenanceOrchestrator(store, svc);
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

    // ─── DedupEngine standalone cache coherence (#17) ────────────────────
    //
    // A standalone DedupAsync (no `write:` arg) now opens its own RunBulkAsync scope so its
    // merge writes invalidate the recall cache. Before #17 the engine wrote through the store
    // directly and called InvalidateRecallCache by hand; these pin the new own-scope behavior
    // through the public recall surface.

    [Fact]
    public async Task DedupEngine_standalone_run_invalidates_the_recall_cache()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        // Two near-identical entries of the same type — Jaccard well above the 0.85 lexical
        // threshold, so the in-process lexical pass merges them (InMemoryEidetStore returns []
        // from FindNearDuplicatesAsync, so the semantic pass never fires here).
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/dedup-high",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "The deployment pipeline runs database migrations before starting the application server",
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
            Importance = 0.8f,
        });
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/dedup-low",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "The deployment pipeline runs the database migrations before starting the application server",
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
            Importance = 0.4f,
        });

        // Warm the cache: both entries surface for this query, so a stale post-merge hit
        // would still return two.
        var before = await svc.RecallAsync("repo-a", "deployment");
        Assert.Equal(2, before.Count);

        var engine = new DedupEngine(store, svc);
        var result = await engine.DedupAsync("repo-a");   // no write: arg → own RunBulkAsync scope
        Assert.Equal(1, result.MergedCount);

        // Load-bearing assertion: WITHOUT the own-scope invalidation this recall would serve
        // the stale pre-merge cache and still return both entries. The discarded entry now has
        // ValidUntil set, so a coherent recall returns only the survivor.
        var after = await svc.RecallAsync("repo-a", "deployment");
        Assert.Single(after);
        Assert.Equal("memories/repo-a/insight/dedup-high", after[0].Id);
    }

    [Fact]
    public async Task DedupEngine_standalone_dryRun_leaves_a_warmed_recall_unchanged()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);

        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/dedup-high",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "The deployment pipeline runs database migrations before starting the application server",
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
            Importance = 0.8f,
        });
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/dedup-low",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "The deployment pipeline runs the database migrations before starting the application server",
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
            Importance = 0.4f,
        });

        var before = await svc.RecallAsync("repo-a", "deployment");
        Assert.Equal(2, before.Count);

        var engine = new DedupEngine(store, svc);
        var result = await engine.DedupAsync("repo-a", dryRun: true);

        // The would-merge pair is reported, but no write reaches the store.
        Assert.Equal(1, result.MergedCount);

        // Recall is unchanged — dry run does NOT invalidate, so the warmed result is served
        // intact. (The engine mutates the in-memory candidate's ValidUntil in place even on a
        // dry run — a test-double artifact, since no UpdateAsync persists it; the cache-coherence
        // contract is asserted at the recall boundary, exactly as DedupEngineTests.DryRun_* does.)
        var after = await svc.RecallAsync("repo-a", "deployment");
        Assert.Equal(2, after.Count);
    }

    // ─── Maintenance run through a JOINED engine stage (#17) ─────────────
    //
    // ConsolidationStage routes through ConsolidationEngine.ConsolidateAsync(write: ctx.Write),
    // joining the orchestrator's single RunBulkAsync scope. The new insight it creates must
    // surface in a recall warmed before the run, proving the joined write invalidated through
    // that one scope (no per-stage hand invalidation remains).

    [Fact]
    public async Task Maintenance_consolidation_stage_invalidates_via_the_joined_bulk_scope()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var now = DateTime.UtcNow;

        // Three tag-overlapping observations (shared "deploy" tag → one TagOverlapGrouper group
        // of size >= 3). InMemoryEidetStore.FindDuplicateAsync returns null, so consolidation
        // creates a new insight (never boosts).
        for (var i = 0; i < 3; i++)
        {
            await store.StoreAsync(new MemoryEntry
            {
                Id = $"memories/repo-a/observation/consolidate-{i}",
                RepoId = "repo-a",
                Type = MemoryType.Observation,
                Content = $"The release runs database migrations during deployment step {i}",
                Tags = ["deploy"],
                CreatedAt = now,
                Validity = new Validity { ValidFrom = now },
                IsLatest = true,
                Importance = 0.6f,
            });
        }

        // Warm the cache: only the 3 observations match before the run.
        var before = await svc.RecallAsync("repo-a", "migrations");
        Assert.Equal(3, before.Count);

        var orch = new MaintenanceOrchestrator(store, svc);
        var report = await orch.RunAsync(new MaintenanceRequest
        {
            RepoId = "repo-a",
            IsRepoActive = true,
            OnlyStages = new HashSet<string> { ConsolidationStage.StageName },
        });
        Assert.Equal(1, report.AffectedBy(ConsolidationStage.StageName));

        // Load-bearing assertion: the consolidation insight (content copied from a representative
        // observation, so it matches "migrations") now surfaces. Without the joined write
        // invalidating the orchestrator's single scope, recall would still serve the warmed 3.
        var after = await svc.RecallAsync("repo-a", "migrations");
        Assert.Equal(4, after.Count);
        Assert.Contains(after, r => r.Type == MemoryType.Insight);
    }
}
