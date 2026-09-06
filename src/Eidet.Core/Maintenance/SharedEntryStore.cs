using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Maintenance;

/// <summary>
/// One maintenance pass, one object per memory.
///
/// Stages run in sequence over one repo and most do the same three things: read a scored page of
/// entries, mutate a field, write the whole entry back. Independently that is fine; in sequence it
/// loses data. Each stage re-reads from the index, so a stage writing late in the pass writes a copy
/// loaded <i>before</i> an earlier stage's write, and persisting the whole document reverts the
/// earlier stage's field — last writer wins on every field, not just the one it meant to change.
///
/// This is a latent hazard demonstrated by test (`SharedEntryStoreTests`, under a query view that lags
/// the pass's own writes, which is what RavenDB gives you), not the diagnosis of a specific field
/// incident: the surviving chain-of-thought entities that prompted this class turned out to have a
/// different cause entirely — `EntityExtractor` re-deriving what repair had just dropped, see
/// `EntityRefillConvergenceTests`. Both are real; only the second one was firing.
///
/// This decorator removes the failure mode instead of asking each stage to defend against it: every
/// read resolves through an id → instance map, so the second stage to see a memory gets the SAME
/// object the first one mutated, and its write carries both edits. Only object identity is collapsed
/// — ordering, limits, filters, and scoring stay the inner store's, and nothing here writes or
/// caches across passes.
///
/// Scope is deliberately one pass: the orchestrator builds one per run and drops it. The accepted
/// residual is the mirror image — an entry already loaded this pass does not pick up a concurrent
/// write from an agent session, so that write is reverted instead. That window existed before,
/// between any single stage's own read and write, and is now as long as the pass; closing it needs
/// optimistic concurrency on the write path, not a narrower map here.
/// </summary>
internal sealed class SharedEntryStore(IEidetStore inner) : IEidetStore
{
    private readonly Dictionary<string, MemoryEntry> _canonical = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The canonical instance for this document, adopting <paramref name="entry"/> as canonical the
    /// first time its id is seen. An entry with no id yet cannot be keyed and is passed through — it
    /// is a not-yet-stored draft, which by definition no other stage is holding.
    /// </summary>
    private MemoryEntry Share(MemoryEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id)) return entry;
        if (_canonical.TryGetValue(entry.Id, out var known)) return known;
        _canonical[entry.Id] = entry;
        return entry;
    }

    private List<MemoryEntry> Share(List<MemoryEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++) entries[i] = Share(entries[i]);
        return entries;
    }

    private IReadOnlyList<MemoryEntry> Share(IReadOnlyList<MemoryEntry> entries) =>
        entries.Select(Share).ToList();

    // ── Reads that hand out entries: the whole point of the class ──

    public async Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) =>
        await inner.GetAsync(id, ct) is { } e ? Share(e) : null;

    public async Task<IReadOnlyDictionary<string, MemoryEntry?>> GetManyAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var resolved = await inner.GetManyAsync(ids, ct);
        var shared = new Dictionary<string, MemoryEntry?>(resolved.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in resolved) shared[id] = entry is null ? null : Share(entry);
        return shared;
    }

    public async Task<List<MemoryEntry>> FullTextSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Share(await inner.FullTextSearchAsync(repoIds, query, ct));

    public async Task<List<MemoryEntry>> VectorSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Share(await inner.VectorSearchAsync(repoIds, query, ct));

    public async Task<IReadOnlyList<ScoredHit>> SearchScoredAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        (await inner.SearchScoredAsync(arm, repoIds, query, ct))
        .Select(h => h with { Entry = Share(h.Entry) })
        .ToList();

    public async Task<IReadOnlyList<MemoryEntry>> FindByEntitiesAsync(
        IReadOnlyList<string> repoIds, IReadOnlyCollection<string> entities,
        IReadOnlyCollection<string> excludeIds, int max, CancellationToken ct = default) =>
        Share(await inner.FindByEntitiesAsync(repoIds, entities, excludeIds, max, ct));

    public async Task<MemoryEntry?> FindDuplicateAsync(
        string repoId, string content, float threshold, CancellationToken ct = default) =>
        await inner.FindDuplicateAsync(repoId, content, threshold, ct) is { } e ? Share(e) : null;

    public async Task<MemoryEntry?> FindDuplicateOfTypeAsync(
        string repoId, MemoryType type, string content, float threshold, CancellationToken ct = default) =>
        await inner.FindDuplicateOfTypeAsync(repoId, type, content, threshold, ct) is { } e ? Share(e) : null;

    public async Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default) =>
        Share(await inner.FindNearDuplicatesAsync(repoId, entry, minSimilarity, max, ct));

    public async Task<IReadOnlyList<MemoryEntry>> GetInvalidatedAsync(
        string repoId, int max, CancellationToken ct = default) =>
        Share(await inner.GetInvalidatedAsync(repoId, max, ct));

    public async Task<IReadOnlyList<MemoryEntry>> GetUnprovenancedAsync(
        string repoId, IReadOnlyCollection<string> repairableSources, int limit, CancellationToken ct = default) =>
        Share(await inner.GetUnprovenancedAsync(repoId, repairableSources, limit, ct));

    public async Task<List<MemoryEntry>> GetTopScoredAsync(
        string repoId, MemoryType[] types, int limit, CancellationToken ct = default) =>
        Share(await inner.GetTopScoredAsync(repoId, types, limit, ct));

    public async Task<List<MemoryEntry>> GetUnenrichedAsync(
        string repoId, int limit, CancellationToken ct = default) =>
        Share(await inner.GetUnenrichedAsync(repoId, limit, ct));

    public async Task<List<MemoryEntry>> BrowseAsync(
        string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default) =>
        Share(await inner.BrowseAsync(repoId, skip, take, type, ct));

    public async Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) =>
        Share(await inner.GetByLayerIdAsync(layerId, ct));

    // ── Writes: adopt what was stored, and forget what was removed ──

    /// <summary>Adopts the stored entry so a later read in this pass returns the caller's instance.</summary>
    public async Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        var id = await inner.StoreAsync(entry, ct);
        Share(entry);
        return id;
    }

    public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        Share(entry);
        return inner.UpdateAsync(entry, ct);
    }

    /// <summary>Evicts the id: a document whose stored form was replaced out from under us must be
    /// re-read rather than served from a map entry that no longer describes it.</summary>
    public Task<bool> ForgetAsync(string id, CancellationToken ct = default)
    {
        _canonical.Remove(id);
        return inner.ForgetAsync(id, ct);
    }

    public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default)
    {
        _canonical.Remove(id);
        return inner.HardDeleteAsync(id, ct);
    }

    // ── Everything else: straight delegation. A member added to IEidetStore and left out of this
    //    class would silently fall back to the interface default (often [] or a no-op) instead of
    //    reaching the real store, so SharedEntryStoreTests asserts every member is declared here.

    public Task PatchAccessAsync(string entryId, DateTime lastAccessedAt, double? lexShare = null, CancellationToken ct = default) =>
        inner.PatchAccessAsync(entryId, lastAccessedAt, lexShare, ct);

    public Task<double?> GetRepoAlphaAsync(string repoId, CancellationToken ct = default) =>
        inner.GetRepoAlphaAsync(repoId, ct);

    public Task UpdateRepoAlphaAsync(string repoId, AlphaEwmaUpdate update, CancellationToken ct = default) =>
        inner.UpdateRepoAlphaAsync(repoId, update, ct);

    public Task<DateTime?> GetLastReflectedAtAsync(string repoId, CancellationToken ct = default) =>
        inner.GetLastReflectedAtAsync(repoId, ct);

    public Task SetLastReflectedAtAsync(string repoId, DateTime whenUtc, CancellationToken ct = default) =>
        inner.SetLastReflectedAtAsync(repoId, whenUtc, ct);

    public Task<string?> GetGitIntakeWatermarkAsync(string repoId, CancellationToken ct = default) =>
        inner.GetGitIntakeWatermarkAsync(repoId, ct);

    public Task SetGitIntakeWatermarkAsync(string repoId, string sha, CancellationToken ct = default) =>
        inner.SetGitIntakeWatermarkAsync(repoId, sha, ct);

    public Task<HashSet<string>> GetConsolidatedSourceIdsAsync(string repoId, CancellationToken ct = default) =>
        inner.GetConsolidatedSourceIdsAsync(repoId, ct);

    public Task<UnenrichedStats> GetUnenrichedStatsAsync(string? repoId = null, CancellationToken ct = default) =>
        inner.GetUnenrichedStatsAsync(repoId, ct);

    public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
        inner.GetCountsByTypeAsync(repoId, ct);

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => inner.TestConnectionAsync(ct);

    public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => inner.GetDatabaseInfoAsync(ct);

    public Task EnsureIndexesAsync(CancellationToken ct = default) => inner.EnsureIndexesAsync(ct);

    public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default) => inner.GetDistinctRepoIdsAsync(ct);

    public Task<Dictionary<string, int>> GetLiveCountsByRepoAsync(CancellationToken ct = default) =>
        inner.GetLiveCountsByRepoAsync(ct);

    public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default) =>
        inner.StoreMountedLayerAsync(layer, ct);

    public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default) =>
        inner.UnmountLayerAsync(layerId, ct);

    public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default) =>
        inner.GetMountedLayersAsync(repoId, ct);

    public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default) =>
        inner.GetLayerAsync(layerId, ct);
}
