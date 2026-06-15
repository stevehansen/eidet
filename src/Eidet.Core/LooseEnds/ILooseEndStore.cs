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
}
