using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Text;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The authority on corpus repair and the enrichment backfill agreeing about the entity field.
///
/// They used to disagree, and the disagreement was a closed loop: repair dropped an entity
/// `EntityHygiene` rejects, the backfill saw `Entities.Count == 0` and re-derived the SAME noise from
/// the same content with `EntityExtractor.Extract`, and both stages reported `Affected: 1` on every
/// pass while the document never changed. Four consecutive full passes over a field repo left a
/// 122-char run-on entity in place with a whole-repo before/after diff of zero changed documents —
/// the counts said work was happening and nothing was.
///
/// The fix is that hygiene lives at the derivation point rather than only in the repair that follows
/// it, so re-deriving cannot reintroduce what repair just removed. These tests pin the loop shut from
/// both ends: the extractor never emits noise, and a second pipeline round is a genuine no-op.
/// </summary>
public class EntityRefillConvergenceTests
{
    private const string Repo = "refill-repo";
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The content that produced the field corpus's surviving run-on: a URL immediately followed by prose.</summary>
    private const string RunOnContent =
        "GET /api/mesh/detail/deletion requires the registration secret. Production verification on "
        + "2026-07-10 confirmed anonymous GETs to tasks/task/progress detail routes and both SSE routes "
        + "now require a valid device credential.";

    private static MemoryEntry Mem(string id, string content, string[] entities) => new()
    {
        Id = $"memories/{Repo}/insight/{id}",
        RepoId = Repo,
        Type = MemoryType.Insight,
        Source = "claude-session",
        Provenance = MemoryProvenance.AgentInferred,
        Content = content,
        OneLiner = "Device credentials are required for the mesh detail routes.",
        Entities = [.. entities],
        Importance = 0.5f,
        Confidence = 0.6f,
        CreatedAt = Origin,
        Validity = new Validity { ValidFrom = Origin },
        IsLatest = true,
    };

    private static async Task<(int Repair, int Backfill)> RunPassAsync(InMemoryEidetStore store)
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
            var repair = await new CorpusRepairStage().ExecuteAsync(ctx, default);
            var backfill = await new HeuristicEnrichmentBackfillStage().ExecuteAsync(ctx, default);
            return (repair.Affected, backfill.Affected);
        });
    }

    /// <summary>
    /// The load-bearing half: whatever the extractor derives must already satisfy the predicate that
    /// repair applies, or the two disagree by construction no matter how either is ordered.
    /// </summary>
    [Fact]
    public void Extraction_output_is_already_clean()
    {
        var extracted = EntityExtractor.Extract(RunOnContent);

        Assert.DoesNotContain(extracted, EntityHygiene.IsNoise);
        Assert.Equal(extracted, EntityHygiene.Clean(extracted));
    }

    [Fact]
    public void Extraction_does_not_emit_a_run_on_that_spills_into_prose()
    {
        var extracted = EntityExtractor.Extract(RunOnContent);

        Assert.DoesNotContain(extracted, e => e.Length > 120);
        Assert.DoesNotContain(extracted, e => e.Count(c => c == ' ') >= 6);
    }

    /// <summary>
    /// The loop itself. Round one may legitimately do work — the seeded entities are noise. Round two
    /// must not: two stages reporting work on an unchanged corpus is the signature of them undoing each
    /// other, which is what ran nightly for months without a single failing assertion.
    /// </summary>
    [Fact]
    public async Task A_second_round_of_repair_then_backfill_is_a_no_op()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("a", RunOnContent, [
            "/detail/deletion requires the registration secret. Production verification on 2026-07-10 confirmed anonymous GETs to tasks",
            "/task/progress detail routes and both SSE routes now require a valid device credential",
        ]));

        var first = await RunPassAsync(store);
        var second = await RunPassAsync(store);
        var third = await RunPassAsync(store);

        Assert.Equal(1, first.Repair);
        Assert.Equal((0, 0), second);
        Assert.Equal((0, 0), third);
    }

    /// <summary>
    /// Repair emptying the field and the backfill refilling it is *correct* behaviour — the defect was
    /// only that the refill reintroduced noise. A memory whose content yields real identifiers should
    /// still end the pass with them.
    /// </summary>
    [Fact]
    public async Task Backfill_still_refills_an_emptied_field_with_real_identifiers()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("a", "The scheduler uses RavenDB Refresh via /api/eidet/context.", ["1. Project names"]));

        await RunPassAsync(store);

        var entry = await store.GetAsync($"memories/{Repo}/insight/a");
        Assert.NotEmpty(entry!.Entities);
        Assert.DoesNotContain(entry.Entities, EntityHygiene.IsNoise);
    }
}
