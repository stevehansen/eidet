namespace Eidet.Core.LooseEnds;

/// <summary>
/// Storage port for Loose Ends — a separate collection from <c>memories/*</c> so no maintenance
/// stage ever enumerates open work. Raven adapter in prod, in-memory fake in tests.
/// </summary>
public interface ILooseEndStore
{
    Task<string> StoreAsync(LooseEnd e, CancellationToken ct = default);
    Task<LooseEnd?> GetAsync(string id, CancellationToken ct = default);
    Task UpdateAsync(LooseEnd e, CancellationToken ct = default);
    Task<IReadOnlyList<LooseEnd>> ListOpenAsync(string repoId, int max, CancellationToken ct = default);
    Task<IReadOnlyList<LooseEnd>> FindOpenByTagsAsync(string repoId, IReadOnlyList<string> tags, int max, CancellationToken ct = default);

    /// <summary>Count of open Loose Ends for a repo — bounded server-side, never materializes the set.</summary>
    Task<int> CountOpenAsync(string repoId, CancellationToken ct = default);

    /// <summary>Atomically claim an Open end for resolution (Open→Resolving). Returns true iff THIS caller won the
    /// claim; false if the end was not Open (already Resolving/Resolved, or gone). The Raven adapter makes this atomic
    /// with optimistic concurrency; the default impl is a non-atomic read-check-write sufficient for single-threaded fakes.</summary>
    async Task<bool> TryClaimForResolveAsync(string id, CancellationToken ct = default)
    {
        var end = await GetAsync(id, ct);
        if (end is null || end.State != LooseEndState.Open) return false;
        end.State = LooseEndState.Resolving;
        await UpdateAsync(end, ct);
        return true;
    }
}
