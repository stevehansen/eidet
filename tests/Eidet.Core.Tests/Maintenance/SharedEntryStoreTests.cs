using System.Reflection;
using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The authority on one maintenance pass holding one object per memory.
///
/// The bug this pins down was invisible to the whole suite for a reason worth stating: every fake in
/// the suite hands out the same <see cref="MemoryEntry"/> instance it stores, so stages accidentally
/// shared state and a whole-document write could not revert anything. RavenDB materializes a fresh
/// object per query, so in production the last stage to write a document won on every field — corpus
/// repair's entity scrub was computed nightly and discarded by importance decay four stages later.
/// <see cref="MaterializingStore"/> exists to reproduce that: it clones on read and on write, which is
/// the only property of the real store that matters here.
/// </summary>
public class SharedEntryStoreTests
{
    private const string Repo = "shared-store-repo";
    private static readonly DateTime LongAgo = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A chain-of-thought leak: entity hygiene drops it, so corpus repair edits this entry.</summary>
    private const string NoiseEntity = "The user wants me to act as an information extractor";

    private static MemoryEntry Mem(string id, params string[] entities) => new()
    {
        Id = $"memories/{Repo}/insight/{id}",
        RepoId = Repo,
        Type = MemoryType.Insight,
        Source = "claude-session",
        Provenance = MemoryProvenance.AgentInferred,
        Content = "The scheduler uses RavenDB Refresh as its alarm clock.",
        Entities = [.. entities],
        Importance = 0.9f,
        Confidence = 0.5f,
        // Old enough that importance decay considers it, which is what makes it a second writer.
        CreatedAt = LongAgo,
        Validity = new Validity { ValidFrom = LongAgo },
        IsLatest = true,
    };

    /// <summary>
    /// An <see cref="InMemoryEidetStore"/> that behaves like a document database: reads materialize a
    /// new object, writes persist a snapshot. Without this the suite cannot tell instance sharing from
    /// its absence.
    /// </summary>
    private class MaterializingStore : InMemoryEidetStore
    {
        protected static MemoryEntry Clone(MemoryEntry e) =>
            JsonSerializer.Deserialize<MemoryEntry>(JsonSerializer.Serialize(e))!;

        public override async Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) =>
            await base.GetAsync(id, ct) is { } e ? Clone(e) : null;

        public override async Task<List<MemoryEntry>> GetTopScoredAsync(
            string repoId, MemoryType[] types, int limit, CancellationToken ct = default) =>
            [.. (await base.GetTopScoredAsync(repoId, types, limit, ct)).Select(Clone)];

        public override async Task<List<MemoryEntry>> FullTextSearchAsync(
            IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
            [.. (await base.FullTextSearchAsync(repoIds, query, ct)).Select(Clone)];

        public override Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) =>
            base.UpdateAsync(Clone(entry), ct);
    }

    /// <summary>
    /// The same store with the one property that turns instance sharing from tidiness into
    /// correctness: <b>queries are index-backed and the index lags</b>. Document loads
    /// (<c>GetAsync</c>) stay immediately consistent, exactly as RavenDB behaves, while every query in
    /// the pass answers from the view as it was before the pass started writing.
    ///
    /// This is what makes a late stage's copy stale. Without it a fake writes and re-reads its own
    /// writes instantly, so no stage can revert another and the suite reports health that production
    /// does not have — which is precisely what happened.
    ///
    /// Distinct from the namespace-level <c>LaggingIndexStore</c>, which models a *validity* lag on the
    /// semantic arm (retirements the index has not noticed) rather than a stale whole-entry read.
    /// </summary>
    private sealed class StaleQueryStore : MaterializingStore
    {
        private List<MemoryEntry>? _indexView;

        public override async Task<List<MemoryEntry>> GetTopScoredAsync(
            string repoId, MemoryType[] types, int limit, CancellationToken ct = default)
        {
            _indexView ??= await base.GetTopScoredAsync(repoId, Enum.GetValues<MemoryType>(), int.MaxValue, ct);
            return [.. _indexView.Where(e => types.Contains(e.Type)).Take(limit).Select(Clone)];
        }
    }

    [Fact]
    public async Task Two_reads_of_one_memory_hand_back_the_same_object()
    {
        var inner = new MaterializingStore();
        await inner.StoreAsync(Mem("a"));
        var shared = new SharedEntryStore(inner);

        var first = (await shared.GetTopScoredAsync(Repo, [MemoryType.Insight], 10))[0];
        var second = await shared.GetAsync($"memories/{Repo}/insight/a");

        Assert.Same(first, second);
        // The premise: without sharing these are two objects, so a write of one discards the other's edits.
        Assert.NotSame(
            (await inner.GetTopScoredAsync(Repo, [MemoryType.Insight], 10))[0],
            await inner.GetAsync($"memories/{Repo}/insight/a"));
    }

    [Fact]
    public async Task An_edit_made_through_one_read_is_visible_through_the_next()
    {
        var inner = new MaterializingStore();
        await inner.StoreAsync(Mem("a"));
        var shared = new SharedEntryStore(inner);

        (await shared.GetTopScoredAsync(Repo, [MemoryType.Insight], 10))[0].OneLiner = "edited in flight";

        var reread = await shared.GetAsync($"memories/{Repo}/insight/a");
        Assert.Equal("edited in flight", reread!.OneLiner);
    }

    /// <summary>Sharing collapses identity only — what a query selects, and in what order, is the inner store's.</summary>
    [Fact]
    public async Task Query_results_are_not_reordered_or_filtered()
    {
        var inner = new MaterializingStore();
        foreach (var id in new[] { "a", "b", "c" }) await inner.StoreAsync(Mem(id));
        var shared = new SharedEntryStore(inner);

        var direct = (await inner.GetTopScoredAsync(Repo, [MemoryType.Insight], 2)).Select(e => e.Id);
        var through = (await shared.GetTopScoredAsync(Repo, [MemoryType.Insight], 2)).Select(e => e.Id);

        Assert.Equal(direct, through);
    }

    /// <summary>
    /// The regression, with the real pair that exposed it: corpus repair scrubs the entity, importance
    /// decay writes the same document four stages later off a stale index read. The decay assertion is
    /// not decoration — a run where decay wrote nothing would pass this test while proving nothing.
    /// </summary>
    [Fact]
    public async Task A_later_stage_write_does_not_revert_an_earlier_stage_edit()
    {
        var store = new StaleQueryStore();
        await store.StoreAsync(Mem("a", "RavenDB", NoiseEntity, "Refresh"));

        var svc = new MemoryService(store);
        var decayed = await svc.RunBulkAsync(async write =>
        {
            var ctx = MaintenanceContext.ForTest(store, write, repoId: Repo);
            await new CorpusRepairStage().ExecuteAsync(ctx, default);
            return await new ImportanceDecayStage().ExecuteAsync(ctx, default);
        });

        Assert.True(decayed.Affected > 0, "importance decay must have written, or nothing could have reverted");
        var entry = await store.GetAsync($"memories/{Repo}/insight/a");
        Assert.Equal(["RavenDB", "Refresh"], entry!.Entities);
    }

    /// <summary>
    /// A member added to <see cref="IEidetStore"/> and forgotten here would not fail to compile: the
    /// interface's default implementation would take over and quietly answer <c>[]</c>, <c>null</c>, or
    /// nothing at all instead of reaching the real store. Dedup losing near-duplicate candidates that
    /// way is a silent behavior change, so the decorator's completeness is asserted rather than trusted.
    /// </summary>
    [Fact]
    public void Every_store_member_is_delegated_rather_than_defaulted()
    {
        var map = typeof(SharedEntryStore).GetInterfaceMap(typeof(IEidetStore));

        var defaulted = map.InterfaceMethods
            .Zip(map.TargetMethods)
            .Where(pair => pair.Second.DeclaringType != typeof(SharedEntryStore))
            .Select(pair => pair.First.Name)
            .ToList();

        Assert.Empty(defaulted);
    }

    /// <summary>
    /// Forget replaces the stored form outside this pass's knowledge, so the cached instance stops
    /// describing the document and must not be served again.
    /// </summary>
    [Fact]
    public async Task Forget_evicts_the_shared_instance()
    {
        var inner = new MaterializingStore();
        await inner.StoreAsync(Mem("a"));
        var shared = new SharedEntryStore(inner);
        var id = $"memories/{Repo}/insight/a";

        var before = await shared.GetAsync(id);
        await shared.ForgetAsync(id);
        var after = await shared.GetAsync(id);

        Assert.NotSame(before, after);
        Assert.NotNull(after!.Validity.ValidUntil);
    }
}
