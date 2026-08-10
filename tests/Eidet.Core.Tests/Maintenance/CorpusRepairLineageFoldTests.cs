using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The authority on corpus repair's lineage fold: consolidation output that re-derives a cluster another
/// live memory already covers is retired, identified by an identical <c>DerivedFrom</c> set.
///
/// The exact-content fold in the same stage cannot see these, because consolidation re-states its cluster
/// in new words every run. Measured on a real corpus: 2,962 of 3,303 live consolidated memories sat in an
/// identical-lineage group, spanning only 450 distinct clusters. Word overlap between two paraphrases of
/// one claim runs about 0.25, far under any content threshold — lineage is what states it exactly.
/// </summary>
public class CorpusRepairLineageFoldTests
{
    private const string Repo = "repair-repo";
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryEntry Mem(
        string id, MemoryType type, string source, string[] derivedFrom, int ageDays, string content)
    {
        var created = Origin.AddDays(ageDays);
        return new MemoryEntry
        {
            Id = $"memories/{Repo}/{type.ToString().ToLowerInvariant()}/{id}",
            RepoId = Repo,
            Type = type,
            Source = source,
            Content = content,
            DerivedFrom = [.. derivedFrom],
            Importance = 0.7f,
            CreatedAt = created,
            Validity = new Validity { ValidFrom = created },
            IsLatest = true,
        };
    }

    /// <summary>An observation cluster's ids — what consolidation cites in <c>DerivedFrom</c>.</summary>
    private static string[] Cluster(string tag, int n) =>
        [.. Enumerable.Range(0, n).Select(i => $"memories/{Repo}/observation/{tag}{i}")];

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
    public async Task Repair_folds_paraphrases_of_one_cluster_and_keeps_the_oldest()
    {
        var store = new InMemoryEidetStore();
        var cluster = Cluster("c", 5);
        // Deliberately unalike wording: a content-similarity fold would keep all four, which is exactly
        // how these accumulated in the field.
        await store.StoreAsync(Mem("k0", MemoryType.Insight, "consolidation", cluster, 0,
            "The parser rejects tabs inside a quoted field."));
        await store.StoreAsync(Mem("k1", MemoryType.Insight, "consolidation", cluster, 5,
            "Quoted values containing horizontal whitespace fail validation."));
        await store.StoreAsync(Mem("k2", MemoryType.Insight, "consolidation", cluster, 10,
            "Escaping rules break down when a delimiter appears mid-token."));
        await store.StoreAsync(Mem("k3", MemoryType.Insight, "consolidation", cluster, 15,
            "Tokenizer strictness causes downstream import errors."));

        await RunRepairAsync(store);

        var live = await LiveAsync(store);
        Assert.Single(live);
        Assert.EndsWith("/k0", live[0].Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_records_a_forget_reason_naming_the_survivor()
    {
        var store = new InMemoryEidetStore();
        var cluster = Cluster("c", 4);
        await store.StoreAsync(Mem("keep", MemoryType.Insight, "consolidation", cluster, 0, "First wording."));
        await store.StoreAsync(Mem("drop", MemoryType.Insight, "consolidation", cluster, 3, "Second wording."));

        await RunRepairAsync(store);

        // Append-only: the copy is closed with a reason, never removed.
        var dropped = await store.GetAsync($"memories/{Repo}/insight/drop");
        Assert.NotNull(dropped!.Validity.ValidUntil);
        Assert.Contains($"memories/{Repo}/insight/keep", dropped.ForgetReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_keeps_consolidation_output_derived_from_a_different_cluster()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("a", MemoryType.Insight, "consolidation", Cluster("a", 4), 0, "Claim about parsing."));
        await store.StoreAsync(Mem("b", MemoryType.Insight, "consolidation", Cluster("b", 4), 1, "Claim about caching."));

        await RunRepairAsync(store);

        Assert.Equal(2, (await LiveAsync(store)).Count);
    }

    [Fact]
    public async Task Repair_keeps_the_two_altitude_pair_over_one_cluster()
    {
        var store = new InMemoryEidetStore();
        var cluster = Cluster("c", 4);
        var fine = Mem("fine", MemoryType.Procedure, "consolidation", cluster, 0, "1. Build. 2. Deploy.");
        // The abstraction cites the fine procedure ahead of the cluster, so their lineage differs by
        // construction — the pair is a deliberate two-altitude emission, not a re-derivation.
        var abstraction = Mem("abs", MemoryType.Procedure, "consolidation",
            [fine.Id, .. cluster], 0, "Run the release script.");
        await store.StoreAsync(fine);
        await store.StoreAsync(abstraction);

        await RunRepairAsync(store);

        Assert.Equal(2, (await LiveAsync(store)).Count);
    }

    [Fact]
    public async Task Repair_leaves_repeated_citations_by_other_writers_alone()
    {
        var store = new InMemoryEidetStore();
        var cluster = Cluster("c", 4);
        // A Canon page cites its approved members; two pages may legitimately cite the same set.
        await store.StoreAsync(Mem("canon1", MemoryType.Insight, "canon-review", cluster, 0, "Term: parser."));
        await store.StoreAsync(Mem("canon2", MemoryType.Insight, "canon-review", cluster, 1, "Term: tokenizer."));

        await RunRepairAsync(store);

        Assert.Equal(2, (await LiveAsync(store)).Count);
    }

    [Fact]
    public async Task Repair_converges_so_a_second_run_changes_nothing()
    {
        var store = new InMemoryEidetStore();
        var cluster = Cluster("c", 5);
        for (var i = 0; i < 4; i++)
            await store.StoreAsync(Mem($"m{i}", MemoryType.Insight, "consolidation", cluster, i * 3,
                $"Distinct wording number {i} covering unrelated vocabulary {i}."));

        var first = await RunRepairAsync(store);
        var second = await RunRepairAsync(store);

        Assert.Equal(3, first.Affected);
        Assert.Equal(0, second.Affected);
    }
}
