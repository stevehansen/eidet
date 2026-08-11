using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The authority on corpus repair retiring body-less intake memories — a heading with no knowledge
/// under it. Intake now rejects these at the gate; this clears what earlier builds banked.
///
/// Measured on a field corpus: 1,000 live heading-only memories across 74 repos (9% of everything
/// live), 843 with an LLM-generated one-liner asserting a claim the repo never made. Because L1
/// prefers the one-liner, 59 wake-up lines across 26 repos were fabrications and one repo spent 8 of
/// its 20 slots on them.
///
/// Entity hygiene is exercised here too: it runs in the same per-entry pass and is repair for the
/// same reason — entities are derived retrieval keys, so re-deriving them is not a content edit.
/// </summary>
public class CorpusRepairBodylessTests
{
    private const string Repo = "bodyless-repo";
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryEntry Mem(
        string id, string content, MemoryProvenance provenance = MemoryProvenance.Intake,
        string? oneLiner = null, string[]? entities = null)
    {
        return new MemoryEntry
        {
            Id = $"memories/{Repo}/insight/{id}",
            RepoId = Repo,
            Type = MemoryType.Insight,
            Source = provenance == MemoryProvenance.Intake ? "intake" : "claude-session",
            Provenance = provenance,
            Content = content,
            OneLiner = oneLiner,
            Entities = [.. entities ?? []],
            Importance = 0.5f,
            CreatedAt = Origin,
            Validity = new Validity { ValidFrom = Origin },
            IsLatest = true,
        };
    }

    private static async Task<StageOutcome> RunRepairAsync(InMemoryEidetStore store)
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
                Budget = new BudgetConfig(),
                Deprecate = new DeprecateConfig { Enabled = false },
            };
            return await new CorpusRepairStage().ExecuteAsync(ctx, default);
        });
    }

    private static async Task<List<MemoryEntry>> LiveAsync(InMemoryEidetStore store) =>
        [.. (await store.GetTopScoredAsync(Repo, Enum.GetValues<MemoryType>(), 500))
            .Where(e => e.Validity.ValidUntil is null)];

    [Fact]
    public async Task Retires_heading_only_intake_memories_and_keeps_the_ones_with_a_body()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("a", "## Architecture"));
        await store.StoreAsync(Mem("b", "## Development Patterns",
            oneLiner: "Focus on iterative development cycles for faster product improvements."));
        await store.StoreAsync(Mem("c", "### Docker\n```bash"));
        await store.StoreAsync(Mem("d", "## Build\nRun dotnet build before the test suite."));

        await RunRepairAsync(store);

        var live = await LiveAsync(store);
        Assert.Single(live);
        Assert.EndsWith("/d", live[0].Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fabricated one-liner is the reason this retires rather than just clearing the field: with the
    /// one-liner gone the render falls through to the heading itself, which is no better.
    /// </summary>
    [Fact]
    public async Task Retirement_reason_records_that_the_rendered_form_was_invented()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("a", "## Development Patterns",
            oneLiner: "Focus on iterative development cycles for faster product improvements."));

        await RunRepairAsync(store);

        // Fetched by id, not by scan: a retired memory is deliberately absent from the scored pool, and
        // it must stay readable through history rather than vanish (append-only).
        var entry = await store.GetAsync($"memories/{Repo}/insight/a");
        Assert.NotNull(entry);
        Assert.NotNull(entry.Validity.ValidUntil);
        Assert.Contains("heading with no body", entry.ForgetReason);
    }

    /// <summary>
    /// Scoped to intake, the only writer that mints these mechanically. A deliberately terse memory an
    /// agent or a user wrote is theirs to keep — this rule is a cleanup of a generator, not a judgement
    /// about how short a thought may be.
    /// </summary>
    [Fact]
    public async Task Leaves_a_body_less_memory_from_another_writer_alone()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("agent", "## Notes", MemoryProvenance.AgentInferred));
        await store.StoreAsync(Mem("intake", "## Notes"));

        await RunRepairAsync(store);

        var live = await LiveAsync(store);
        Assert.Single(live);
        Assert.EndsWith("/agent", live[0].Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_run_over_a_repaired_corpus_changes_nothing()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("a", "## Architecture"));
        await store.StoreAsync(Mem("b", "## Quick Links"));
        await store.StoreAsync(Mem("c", "## Build\nRun dotnet build before the test suite."));

        var first = await RunRepairAsync(store);
        var second = await RunRepairAsync(store);

        Assert.Equal(2, first.Affected);
        Assert.Equal(0, second.Affected);
    }

    [Fact]
    public async Task Scrubs_chain_of_thought_entities_without_touching_content()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("a", "The scheduler uses RavenDB Refresh as its alarm clock.",
            entities: ["RavenDB", "The user wants me to act as an information extractor", "1. Project names", "Refresh"]));

        var outcome = await RunRepairAsync(store);

        var entry = Assert.Single(await LiveAsync(store));
        Assert.Equal(["RavenDB", "Refresh"], entry.Entities);
        Assert.Equal("The scheduler uses RavenDB Refresh as its alarm clock.", entry.Content);
        Assert.Equal(1, outcome.Affected);
    }

    [Fact]
    public async Task Leaves_clean_entities_untouched_so_the_stage_stays_a_no_op()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("a", "The scheduler uses RavenDB Refresh as its alarm clock.",
            entities: ["RavenDB", "Refresh"]));

        var outcome = await RunRepairAsync(store);

        Assert.Equal(0, outcome.Affected);
    }
}
