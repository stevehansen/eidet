namespace Eidet.Core.Canon;

/// <summary>
/// Storage port for Canon drafts — a separate <c>canondrafts/*</c> collection from <c>memories/*</c>, so
/// no maintenance stage ever enumerates unreviewed synthesis. Raven adapter in prod, in-memory fake in
/// tests. The <see cref="TryClaimForApproveAsync"/> claim gate is the double-mint guard.
/// </summary>
public interface ICanonDraftStore
{
    Task<string> StoreAsync(CanonDraft d, CancellationToken ct = default);
    Task<CanonDraft?> GetAsync(string id, CancellationToken ct = default);
    Task UpdateAsync(CanonDraft d, CancellationToken ct = default);

    /// <summary>Drafts for a repo, newest-proposed first, optionally filtered to a single status, capped at <paramref name="max"/>.</summary>
    Task<IReadOnlyList<CanonDraft>> ListAsync(string repoId, CanonDraftStatus? status, int max, CancellationToken ct = default);

    /// <summary>The one draft keyed by (repo, kind, slug), or null — the damper's lookup for an existing draft.</summary>
    Task<CanonDraft?> FindBySlugAsync(string repoId, CanonKind kind, string slug, CancellationToken ct = default);

    /// <summary>
    /// Atomically claim a Pending draft for approval (Pending→Approving). Returns true iff THIS caller won
    /// the claim; false if the draft was not Pending (already Approving/Approved/Rejected, or gone). The
    /// Raven adapter makes this atomic with optimistic concurrency; the default impl is a non-atomic
    /// read-check-write sufficient for single-threaded fakes.
    /// </summary>
    async Task<bool> TryClaimForApproveAsync(string id, CancellationToken ct = default)
    {
        var d = await GetAsync(id, ct);
        if (d is null || d.Status != CanonDraftStatus.Pending) return false;
        d.Status = CanonDraftStatus.Approving;
        await UpdateAsync(d, ct);
        return true;
    }
}
