using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// End-to-end: when the recall-consistency guard vetoes a dedup merge, nothing is forgotten — the
/// discard stays live, is stamped with <c>LastMergeRejectedAt</c>, and the rejection is counted.
/// </summary>
public class DedupGuardVetoTests
{
    private static MemoryEntry Insight(string id, string content, float importance) => new()
    {
        Id = id,
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = content,
        Importance = importance,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
    };

    [Fact]
    public async Task VetoedMerge_KeepsDiscardLive_StampsAndCounts()
    {
        var store = new GuardVetoStore();
        var keep = Insight("memories/repo-a/insight/keep", "authentication uses short-lived signed session tokens", 0.9f);
        var discard = Insight("memories/repo-a/insight/discard", "login security depends on ephemeral credentials", 0.3f);
        await store.StoreAsync(keep);
        await store.StoreAsync(discard);
        store.SeedNearDuplicate(keep.Id, discard.Id); // dedup's semantic pass will try to fold discard into keep

        var result = await new DedupEngine(store, new MemoryService(store)).DedupAsync("repo-a");

        Assert.Equal(0, result.MergedCount);
        Assert.Equal(1, result.RejectedCount);
        var d = await store.GetAsync(discard.Id);
        Assert.Null(d!.Validity.ValidUntil);           // NOT forgotten
        Assert.NotNull(d.LastMergeRejectedAt);          // stamped for the quality dashboard
    }
}

/// <summary>Dedup fake with seedable near-dups whose vector ranking never contains the survivor, so the
/// guard always vetoes — simulating a corpus where the survivor doesn't surface for the discard's intent.</summary>
internal sealed class GuardVetoStore : InMemoryEidetStore
{
    private readonly Dictionary<string, List<string>> _nearDups = new(StringComparer.OrdinalIgnoreCase);

    public void SeedNearDuplicate(string entryId, string nearDupId)
    {
        if (!_nearDups.TryGetValue(entryId, out var list)) _nearDups[entryId] = list = [];
        list.Add(nearDupId);
    }

    public override async Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default)
    {
        if (!_nearDups.TryGetValue(entry.Id, out var ids)) return [];
        var results = new List<MemoryEntry>();
        foreach (var id in ids)
        {
            var cand = await GetAsync(id, ct);
            if (cand is { IsLatest: true } && cand.Type == entry.Type && cand.Validity.ValidUntil is null)
                results.Add(cand);
        }
        return results.Take(max).ToList();
    }

    // Non-empty ranking that never includes the survivor → RecallConsistencyGuard always vetoes.
    public override Task<List<MemoryEntry>> VectorSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>
        {
            new() { Id = "memories/repo-a/insight/decoy", RepoId = "repo-a", Type = MemoryType.Insight },
        });
}
