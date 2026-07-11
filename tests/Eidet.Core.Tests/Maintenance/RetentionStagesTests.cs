using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Budgeted eviction + deprecate stages (#39) and the derived <see cref="RetentionScore"/> ordering.
/// Stages are driven directly over an in-memory store inside a real bulk-write scope.
/// </summary>
public class RetentionStagesTests
{
    private const string Repo = "ret-repo";

    private static MemoryEntry Mem(
        string id, MemoryType type, float importance, int echo = 0, int fizzle = 0,
        DateTime? created = null, DateTime? lastAccessed = null)
    {
        var now = created ?? DateTime.UtcNow;
        return new MemoryEntry
        {
            Id = $"memories/{Repo}/{type.ToString().ToLowerInvariant()}/{id}",
            RepoId = Repo,
            Type = type,
            Content = $"content for {id} about the parser module and its behavior",
            Importance = importance,
            EchoCount = echo,
            FizzleCount = fizzle,
            CreatedAt = now,
            LastAccessedAt = lastAccessed,
            Validity = new Validity { ValidFrom = now },
            IsLatest = true,
        };
    }

    private static async Task<StageOutcome> RunAsync(InMemoryEidetStore store, IMaintenanceStage stage, BudgetConfig? budget = null, DeprecateConfig? deprecate = null)
    {
        var svc = new MemoryService(store);
        return await svc.RunBulkAsync(async write =>
        {
            var enrich = EnrichmentService.CreateNull();
            var memory = new MemoryService(store);
            var ctx = new MaintenanceContext
            {
                Store = store,
                Write = write,
                Enrichment = enrich,
                Consolidation = new ConsolidationEngine(store, enrich, memory),
                Reflection = new ReflectionEngine(store, enrich, memory),
                Dedup = new DedupEngine(store, memory, enrich),
                Auditor = new Eidet.Core.Integrity.IntegrityAuditor(memory, store),
                RepoId = Repo,
                IsRepoActive = true,
                Budget = budget ?? new BudgetConfig(),
                Deprecate = deprecate ?? new DeprecateConfig { Enabled = false },
            };
            return await stage.ExecuteAsync(ctx, default);
        });
    }

    // ─── RetentionScore ────────────────────────────────────────────────

    [Fact]
    public void RetentionScore_RewardsEchoAndImportance()
    {
        var now = DateTime.UtcNow;
        var echoed = Mem("e", MemoryType.Insight, 0.5f, echo: 20, created: now);
        var plain = Mem("p", MemoryType.Insight, 0.5f, echo: 0, created: now);
        Assert.True(RetentionScore.Of(echoed, now, 0.5) > RetentionScore.Of(plain, now, 0.5));

        var important = Mem("i", MemoryType.Insight, 0.9f, echo: 0, created: now);
        Assert.True(RetentionScore.Of(important, now, 0.5) > RetentionScore.Of(plain, now, 0.5));
    }

    // ─── BudgetEvictionStage ───────────────────────────────────────────

    [Fact]
    public async Task BudgetEviction_Disabled_EvictsNothing()
    {
        var store = new InMemoryEidetStore();
        for (var i = 0; i < 5; i++) await store.StoreAsync(Mem($"m{i}", MemoryType.Insight, 0.1f * i));

        var outcome = await RunAsync(store, new BudgetEvictionStage(), budget: new BudgetConfig { Enabled = false, MaxPerType = 2 });

        Assert.Equal(0, outcome.Affected);
    }

    [Fact]
    public async Task BudgetEviction_EvictsLowestRetentionDownToCap()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        // Equal recency (all created now); retention ∝ importance × echo-reinforcement.
        await store.StoreAsync(Mem("keep-hi", MemoryType.Insight, 0.9f, echo: 5, created: now));
        await store.StoreAsync(Mem("keep-echo", MemoryType.Insight, 0.5f, echo: 10, created: now));
        await store.StoreAsync(Mem("keep-mid", MemoryType.Insight, 0.8f, echo: 0, created: now));
        await store.StoreAsync(Mem("evict-lowest", MemoryType.Insight, 0.2f, echo: 0, created: now));
        await store.StoreAsync(Mem("evict-low", MemoryType.Insight, 0.3f, echo: 0, created: now));

        var outcome = await RunAsync(store, new BudgetEvictionStage(), budget: new BudgetConfig { Enabled = true, MaxPerType = 3 });

        Assert.Equal(2, outcome.Affected);
        Assert.NotNull((await store.GetAsync($"memories/{Repo}/insight/evict-lowest"))!.Validity.ValidUntil);
        Assert.NotNull((await store.GetAsync($"memories/{Repo}/insight/evict-low"))!.Validity.ValidUntil);
        Assert.StartsWith("budget-eviction:", (await store.GetAsync($"memories/{Repo}/insight/evict-lowest"))!.ForgetReason);
        // The three highest-retention survive.
        Assert.Null((await store.GetAsync($"memories/{Repo}/insight/keep-hi"))!.Validity.ValidUntil);
        Assert.Null((await store.GetAsync($"memories/{Repo}/insight/keep-echo"))!.Validity.ValidUntil);
        Assert.Null((await store.GetAsync($"memories/{Repo}/insight/keep-mid"))!.Validity.ValidUntil);
    }

    [Fact]
    public async Task BudgetEviction_NeverEvictsQuarantinedMemory()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        var quarantined = Mem("q-lowest", MemoryType.Insight, 0.1f, created: now); // lowest retention overall
        quarantined.Quarantine = new QuarantineInfo { Released = false, QuarantinedAt = now };
        await store.StoreAsync(quarantined);
        await store.StoreAsync(Mem("a", MemoryType.Insight, 0.5f, created: now));
        await store.StoreAsync(Mem("b", MemoryType.Insight, 0.6f, created: now));
        await store.StoreAsync(Mem("c", MemoryType.Insight, 0.7f, created: now));
        await store.StoreAsync(Mem("d-low", MemoryType.Insight, 0.2f, created: now));

        var outcome = await RunAsync(store, new BudgetEvictionStage(), budget: new BudgetConfig { Enabled = true, MaxPerType = 3 });

        // The quarantined memory is excluded from the candidate set entirely. Of the 4 non-quarantined,
        // the cap of 3 evicts exactly the lowest (d-low) — while q-lowest, the lowest-retention memory
        // overall, is protected and survives.
        Assert.Equal(1, outcome.Affected);
        Assert.Null((await store.GetAsync($"memories/{Repo}/insight/q-lowest"))!.Validity.ValidUntil);
        Assert.NotNull((await store.GetAsync($"memories/{Repo}/insight/d-low"))!.Validity.ValidUntil);
    }

    // ─── DeprecateStage ────────────────────────────────────────────────

    private static MemoryEntry StaleProc(string id, float importance, int echo, int fizzle, double idleDays)
    {
        var now = DateTime.UtcNow;
        var e = Mem(id, MemoryType.Procedure, importance, echo, fizzle, created: now.AddDays(-idleDays - 10));
        e.LastAccessedAt = now.AddDays(-idleDays);
        return e;
    }

    [Fact]
    public async Task Deprecate_RetiresFlooredIdleNetNegativeProcedure()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(StaleProc("terminal", FadeMemCurve.Floor, echo: 1, fizzle: 6, idleDays: 200));

        var outcome = await RunAsync(store, new DeprecateStage(), deprecate: new DeprecateConfig { Enabled = true, MinIdleDays = 180 });

        Assert.Equal(1, outcome.Affected);
        var e = await store.GetAsync($"memories/{Repo}/procedure/terminal");
        Assert.NotNull(e!.Validity.ValidUntil);
        Assert.StartsWith("deprecated:", e.ForgetReason);
    }

    [Fact]
    public async Task Deprecate_SkipsAboveFloor_NetPositive_AndRecentlyUsed()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(StaleProc("above-floor", 0.5f, echo: 1, fizzle: 6, idleDays: 200));   // not floored
        await store.StoreAsync(StaleProc("net-positive", FadeMemCurve.Floor, echo: 6, fizzle: 1, idleDays: 200)); // not net-negative
        await store.StoreAsync(StaleProc("recent", FadeMemCurve.Floor, echo: 1, fizzle: 6, idleDays: 10)); // not idle

        var outcome = await RunAsync(store, new DeprecateStage(), deprecate: new DeprecateConfig { Enabled = true, MinIdleDays = 180 });

        Assert.Equal(0, outcome.Affected);
    }

    [Fact]
    public async Task Deprecate_SkipsQuarantinedProcedure()
    {
        var store = new InMemoryEidetStore();
        var q = StaleProc("q-terminal", FadeMemCurve.Floor, echo: 1, fizzle: 6, idleDays: 200);
        q.Quarantine = new QuarantineInfo { Released = false, QuarantinedAt = DateTime.UtcNow };
        await store.StoreAsync(q);

        var outcome = await RunAsync(store, new DeprecateStage(), deprecate: new DeprecateConfig { Enabled = true, MinIdleDays = 180 });

        Assert.Equal(0, outcome.Affected);
        Assert.Null((await store.GetAsync($"memories/{Repo}/procedure/q-terminal"))!.Validity.ValidUntil);
    }
}
