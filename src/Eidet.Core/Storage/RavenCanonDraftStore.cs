using Eidet.Core.Canon;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace Eidet.Core.Storage;

/// <summary>
/// RavenDB adapter for <see cref="ICanonDraftStore"/>. The <see cref="CanonDraft"/> CLR type lands in its
/// own <c>CanonDrafts</c> collection regardless of the <c>canondrafts/...</c> string id, so no
/// memory-maintenance stage (which all query <c>MemoryEntry</c>/<c>Memories_Search</c>) ever touches
/// unreviewed synthesis. Slug lookups load by deterministic id (strongly consistent, no index staleness);
/// <see cref="TryClaimForApproveAsync"/> is the change-vector CAS that makes the double-mint guard atomic.
/// </summary>
public sealed class RavenCanonDraftStore : ICanonDraftStore
{
    private readonly IDocumentStore _store;

    public RavenCanonDraftStore(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<string> StoreAsync(CanonDraft d, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(d, d.Id, ct);
        await session.SaveChangesAsync(ct);
        return d.Id;
    }

    public async Task<CanonDraft?> GetAsync(string id, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<CanonDraft>(id, ct);
    }

    public async Task UpdateAsync(CanonDraft d, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(d, d.Id, ct);
        await session.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CanonDraft>> ListAsync(
        string repoId, CanonDraftStatus? status, int max, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var query = session.Query<CanonDraft>().Where(d => d.RepoId == repoId);
        if (status is { } s)
            query = query.Where(d => d.Status == s);
        return await query
            .OrderByDescending(d => d.ProposedAt)
            .Take(max)
            .ToListAsync(ct);
    }

    public Task<CanonDraft?> FindBySlugAsync(
        string repoId, CanonKind kind, string slug, CancellationToken ct = default) =>
        // The id is deterministic in (repo, kind, slug), so a direct load is both the cheapest lookup and
        // the only strongly-consistent one — the damper reads a draft it may have created moments earlier
        // in the same regenerate run, before any index has caught up.
        GetAsync(CanonDraftId.For(repoId, kind, slug), ct);

    public async Task<bool> TryClaimForApproveAsync(string id, CancellationToken ct = default)
    {
        // Optimistic concurrency scoped to THIS session so two concurrent claims race on the change vector:
        // exactly one SaveChanges wins Pending→Approving, the loser sees ConcurrencyException and returns
        // false — the atomic gate that stops a double-mint approve (RavenLooseEndStore precedent).
        using var session = _store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var d = await session.LoadAsync<CanonDraft>(id, ct);
        if (d is null || d.Status != CanonDraftStatus.Pending) return false;
        d.Status = CanonDraftStatus.Approving;
        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return false;
        }
    }
}
