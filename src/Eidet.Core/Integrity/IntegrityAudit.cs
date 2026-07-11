namespace Eidet.Core.Integrity;

/// <summary>
/// Every read path a forgotten / superseded / quarantined memory could leak through. This enum is
/// the single enumeration of read paths: adding one here forces a probe for it (the coverage test
/// fails until <see cref="IntegrityAuditor"/> covers the new value). <see cref="GraphNeighbor"/> and
/// <see cref="DuplicateDetection"/> are the two paths the shipped FAMA regression test does not
/// exercise, so the runtime auditor is strictly broader than that test.
/// </summary>
public enum ReadPath { Recall, ContextL1, CrossRepoSearch, GraphNeighbor, DuplicateDetection }

/// <summary>A single leak: a soft-deleted memory that surfaced through a read path, with the evidence.</summary>
public readonly record struct IntegrityLeak(string MemoryId, ReadPath Path, string RepoId, string Evidence);

/// <summary>The outcome of one <see cref="IIntegrityAuditor.VerifyForgottenAsync"/> run.</summary>
public sealed record IntegrityReport(string RepoId, DateTime RanAt, int MemoriesProbed, IReadOnlyList<IntegrityLeak> Leaks)
{
    public bool Clean => Leaks.Count == 0;

    /// <summary>
    /// The distinct read paths actually exercised this run (empty when nothing was invalidated).
    /// Surfaced so the coverage test can pin that the auditor dispatched a probe for every
    /// <see cref="ReadPath"/> value — the guard that a future read path can't silently narrow the guarantee.
    /// </summary>
    public IReadOnlyList<ReadPath> PathsProbed { get; init; } = [];
}

/// <summary>
/// Deep module: enumerates every read path internally and asserts no soft-deleted memory surfaces
/// against live production data — the runtime half of the FAMA forget guarantee (the CI half ships
/// as <c>FamaForgetTests</c>). The single home for the "is this memory supposed to be invisible?"
/// invariant. Catches failure modes a fixture test structurally can't: a stale index that never
/// refreshed after a forget, a corrupted <c>ValidUntil</c>/<c>IsLatest</c>, or a read path added
/// later without the filter.
/// </summary>
public interface IIntegrityAuditor
{
    Task<IntegrityReport> VerifyForgottenAsync(string repoId, CancellationToken ct = default);
}
