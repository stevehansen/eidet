using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Per-stage isolation tests for <see cref="RoiDecayStage"/> (issue #35) — the reversible,
/// Importance-only auto-demote of proven net-negative action memories. Driven directly via
/// <see cref="MaintenanceContext.ForTest"/> inside a real bulk-write scope (mirroring
/// <see cref="MaintenanceStageIsolationTests"/>). Eligibility: Procedure/Heuristic that are
/// IsLatest + unlayered + valid, with ≥3 echo+fizzle feedback AND fizzles &gt; echoes; the new
/// Importance is <c>max(Floor, Importance · MemoryRoi.Factor)</c>, skipped if the change is &lt;1%.
/// It never sets ForgetAfter/Validity, so it is fully reversible.
/// </summary>
public class RoiDecayStageTests
{
    private const string Repo = "test-repo";

    private static MemoryEntry Entry(
        string id,
        MemoryType type = MemoryType.Procedure,
        float importance = 0.6f,
        int echo = 0,
        int fizzle = 0,
        bool isLatest = true,
        string? layerId = null,
        DateTime? validUntil = null) => new()
    {
        Id = $"memories/{Repo}/{type.ToString().ToLowerInvariant()}/{id}",
        RepoId = Repo,
        Type = type,
        Content = $"content for {id}",
        CreatedAt = DateTime.UtcNow.AddDays(-30),
        Validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-30), ValidUntil = validUntil },
        IsLatest = isLatest,
        LayerId = layerId,
        Importance = importance,
        EchoCount = echo,
        FizzleCount = fizzle,
    };

    private static async Task<StageOutcome> RunStageAsync(
        InMemoryEidetStore store, bool isRepoActive = true)
    {
        var svc = new MemoryService(store);
        return await svc.RunBulkAsync(async write =>
        {
            var ctx = isRepoActive
                ? MaintenanceContext.ForTest(store, write, repoId: Repo)
                : InactiveCtx(store, write);
            return await new RoiDecayStage().ExecuteAsync(ctx, default);
        });
    }

    private static MaintenanceContext InactiveCtx(InMemoryEidetStore store, BulkMutationCtx write)
    {
        var memory = new MemoryService(store);
        var enrich = EnrichmentService.CreateNull();
        return new MaintenanceContext
        {
            Store = store,
            Write = write,
            Enrichment = enrich,
            Consolidation = new ConsolidationEngine(store, enrich, memory),
            Reflection = new ReflectionEngine(store, enrich, memory),
            Dedup = new DedupEngine(store, memory, enrich),
            RepoId = Repo,
            IsRepoActive = false,
        };
    }

    // ─── Happy path: net-negative action memory is demoted ────────────────────

    [Fact]
    public async Task NetNegative_procedure_with_enough_feedback_is_demoted_by_roi()
    {
        var store = new InMemoryEidetStore();
        // 0 echo / 5 fizzle ⇒ roi = 3/8 = 0.375; importance 0.6 → 0.225.
        var entry = Entry("bad", echo: 0, fizzle: 5, importance: 0.6f);
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store);

        Assert.Equal(1, outcome.Affected);
        var expected = Math.Max(FadeMemCurve.Floor, 0.6f * (float)MemoryRoi.Factor(entry));
        Assert.Equal(0.225f, entry.Importance, 0.0001f);
        Assert.Equal(expected, entry.Importance, 0.0001f);
    }

    [Fact]
    public async Task NetNegative_heuristic_is_also_demoted()
    {
        var store = new InMemoryEidetStore();
        var entry = Entry("badh", MemoryType.Heuristic, echo: 1, fizzle: 4, importance: 0.7f);
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store);

        Assert.Equal(1, outcome.Affected);
        // roi = (1+3)/(4+3) = 4/7; 0.7 * 4/7 = 0.4.
        Assert.Equal(0.4f, entry.Importance, 0.0001f);
    }

    [Fact]
    public async Task Demoted_importance_is_floored_at_fademem_floor()
    {
        var store = new InMemoryEidetStore();
        // Heavy fizzle on an already-low importance: roi*importance would dip below the floor.
        var entry = Entry("floored", echo: 0, fizzle: 50, importance: 0.07f);
        await store.StoreAsync(entry);

        await RunStageAsync(store);

        Assert.Equal(FadeMemCurve.Floor, entry.Importance);
    }

    // ─── Eligibility gates ────────────────────────────────────────────────────

    [Fact]
    public async Task Below_min_feedback_is_untouched()
    {
        var store = new InMemoryEidetStore();
        // Net-negative but only 2 total feedback (< MinFeedback 3).
        var entry = Entry("thin", echo: 0, fizzle: 2, importance: 0.6f);
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store);

        Assert.Equal(0, outcome.Affected);
        Assert.Equal(0.6f, entry.Importance, 0.0001f);
    }

    [Fact]
    public async Task NetNonnegative_action_memory_is_untouched()
    {
        var store = new InMemoryEidetStore();
        // 5 echo / 4 fizzle: enough feedback but NOT net-negative.
        var entry = Entry("good", echo: 5, fizzle: 4, importance: 0.6f);
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store);

        Assert.Equal(0, outcome.Affected);
        Assert.Equal(0.6f, entry.Importance, 0.0001f);
    }

    [Theory]
    [InlineData(MemoryType.Insight)]
    [InlineData(MemoryType.Observation)]
    public async Task Knowledge_types_are_untouched_even_when_net_negative(MemoryType type)
    {
        var store = new InMemoryEidetStore();
        var entry = Entry("knowledge", type, echo: 0, fizzle: 10, importance: 0.6f);
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store);

        Assert.Equal(0, outcome.Affected);
        Assert.Equal(0.6f, entry.Importance, 0.0001f);
    }

    [Fact]
    public async Task Layered_memory_is_untouched()
    {
        var store = new InMemoryEidetStore();
        // A read-only layer entry: net-negative but LayerId != null → the stage skips it.
        var entry = Entry("layered", echo: 0, fizzle: 5, importance: 0.6f, layerId: "layers/base");
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store);

        Assert.Equal(0, outcome.Affected);
        Assert.Equal(0.6f, entry.Importance, 0.0001f);
    }

    [Fact]
    public async Task NonLatest_and_invalid_entries_are_untouched()
    {
        var store = new InMemoryEidetStore();
        var superseded = Entry("old", echo: 0, fizzle: 5, importance: 0.6f, isLatest: false);
        var expired = Entry("expired", echo: 0, fizzle: 5, importance: 0.6f,
            validUntil: DateTime.UtcNow.AddDays(-1));
        await store.StoreAsync(superseded);
        await store.StoreAsync(expired);

        var outcome = await RunStageAsync(store);

        Assert.Equal(0, outcome.Affected);
        Assert.Equal(0.6f, superseded.Importance, 0.0001f);
        Assert.Equal(0.6f, expired.Importance, 0.0001f);
    }

    [Fact]
    public async Task SubOnePercent_change_is_skipped_without_a_write()
    {
        var store = new CountingStore();
        // roi ≈ 0.9999 (echo just under fizzle with large counts) → change < 1% → skip.
        // (echo+K)/(fizzle+K) with echo=996, fizzle=997 = 999/1000 = 0.999 → 0.1% change → skipped.
        var entry = Entry("tiny", echo: 996, fizzle: 997, importance: 0.6f);
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store);

        Assert.Equal(0, outcome.Affected);
        Assert.Equal(0.6f, entry.Importance, 0.0001f);
        Assert.Equal(0, store.UpdateCount);
    }

    // ─── Reversibility ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Demotion_never_sets_forgetafter_or_validity()
    {
        var store = new InMemoryEidetStore();
        var entry = Entry("rev", echo: 0, fizzle: 5, importance: 0.6f);
        await store.StoreAsync(entry);

        await RunStageAsync(store);

        Assert.Null(entry.ForgetAfter);
        Assert.Null(entry.Validity.ValidUntil);
        Assert.True(entry.IsLatest);
    }

    // ─── Convergence / idempotency ──────────────────────────────────────────────

    [Fact]
    public async Task Repeated_runs_converge_toward_but_never_below_the_floor_and_never_throw()
    {
        var store = new InMemoryEidetStore();
        var entry = Entry("converge", echo: 0, fizzle: 5, importance: 0.6f);
        await store.StoreAsync(entry);

        var prev = entry.Importance;
        for (var i = 0; i < 20; i++)
        {
            await RunStageAsync(store);
            Assert.True(entry.Importance <= prev + 1e-6f,
                $"run {i}: importance {entry.Importance} should not rise above prior {prev}");
            Assert.True(entry.Importance >= FadeMemCurve.Floor,
                $"run {i}: importance {entry.Importance} must stay at/above floor {FadeMemCurve.Floor}");
            prev = entry.Importance;
        }

        // Eventually it lands at the floor and a further run is a no-op (no change ≥ 1%).
        Assert.Equal(FadeMemCurve.Floor, entry.Importance);
        var finalOutcome = await RunStageAsync(store);
        Assert.Equal(0, finalOutcome.Affected);
    }

    // ─── Repo-active gate ────────────────────────────────────────────────────────

    [Fact]
    public async Task Inactive_repo_is_a_no_op()
    {
        var store = new InMemoryEidetStore();
        var entry = Entry("bad", echo: 0, fizzle: 5, importance: 0.6f);
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store, isRepoActive: false);

        Assert.Equal(0, outcome.Affected);
        Assert.Equal(0.6f, entry.Importance, 0.0001f);
    }

    /// <summary>Counts UpdateAsync calls — the write path BulkMutationCtx.WriteAsync routes through.</summary>
    private sealed class CountingStore : InMemoryEidetStore
    {
        public int UpdateCount { get; private set; }

        public override Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            UpdateCount++;
            return base.UpdateAsync(entry, ct);
        }
    }
}
