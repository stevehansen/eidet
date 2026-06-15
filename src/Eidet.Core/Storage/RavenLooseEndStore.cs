using Eidet.Core.LooseEnds;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace Eidet.Core.Storage;

/// <summary>
/// RavenDB adapter for <see cref="ILooseEndStore"/>. The <see cref="LooseEnd"/> CLR type lands in
/// its own <c>LooseEnds</c> collection regardless of the <c>looseends/...</c> string id, so no
/// memory-maintenance stage (which all query <c>MemoryEntry</c>/<c>Memories_Search</c>) ever
/// touches open work — the no-decay invariant is structural. No index in v1; RavenDB auto-indexes
/// the where-clauses. Ordering is done client-side after fetch.
/// </summary>
public sealed class RavenLooseEndStore : ILooseEndStore
{
    private readonly IDocumentStore _store;

    public RavenLooseEndStore(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<string> StoreAsync(LooseEnd e, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(e, e.Id, ct);
        await session.SaveChangesAsync(ct);
        return e.Id;
    }

    public async Task<LooseEnd?> GetAsync(string id, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<LooseEnd>(id, ct);
    }

    public async Task UpdateAsync(LooseEnd e, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(e, e.Id, ct);
        await session.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<LooseEnd>> ListOpenAsync(string repoId, int max, CancellationToken ct = default)
    {
        // Order + page server-side (matches every other query in RavenEidetStore) so the wake-up
        // hot path never streams the whole open set just to take the top few.
        using var session = _store.OpenAsyncSession();
        return await session.Query<LooseEnd>()
            .Where(e => e.RepoId == repoId && e.State == LooseEndState.Open)
            .OrderBy(e => e.Priority).ThenBy(e => e.CreatedAt)
            .Take(max)
            .ToListAsync(ct);
    }

    public async Task<int> CountOpenAsync(string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.Query<LooseEnd>()
            .Where(e => e.RepoId == repoId && e.State == LooseEndState.Open)
            .CountAsync(ct);
    }

    public async Task<IReadOnlyList<LooseEnd>> FindOpenByTagsAsync(
        string repoId, IReadOnlyList<string> tags, int max, CancellationToken ct = default)
    {
        if (tags.Count == 0) return [];

        using var session = _store.OpenAsyncSession();
        var open = await session.Query<LooseEnd>()
            .Where(e => e.RepoId == repoId && e.State == LooseEndState.Open)
            .ToListAsync(ct);

        // Tag overlap is cheap and the open set is small; filter client-side to avoid an index.
        var wanted = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        var matched = open.Where(e => e.Tags.Any(wanted.Contains));
        return Order(matched).Take(max).ToList();
    }

    // Wake-up ordering: highest priority first (Priority 1=high, so ascending), oldest first within
    // a priority tier (surfaces the stalest high-priority work — the backlog-hygiene signal).
    private static IEnumerable<LooseEnd> Order(IEnumerable<LooseEnd> ends) =>
        ends.OrderBy(e => e.Priority).ThenBy(e => e.CreatedAt);
}
