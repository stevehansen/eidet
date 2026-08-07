namespace Eidet.Core.Integrity;

/// <summary>
/// Every integrity invariant the auditor verifies against live data. This enum is the single enumeration
/// of those checks: adding one here forces a probe for it (the dispatch throws and the coverage test fails
/// until <see cref="IntegrityAuditor"/> covers the new value).
///
/// The first five are read paths a forgotten / superseded / quarantined memory could leak through;
/// <see cref="GraphNeighbor"/> and <see cref="DuplicateDetection"/> are the two the shipped FAMA regression
/// test does not exercise, so the runtime auditor is strictly broader than that test. The last four (#80)
/// verify claims about LIVE memories that were previously asserted and never checked: that provenance was
/// established, that content still matches the commitment in its own id, and that lineage citations still
/// resolve to the text they were made against.
/// </summary>
public enum IntegrityCheck
{
    Recall,
    ContextL1,
    CrossRepoSearch,
    GraphNeighbor,

    /// <summary>A forgotten memory reachable through CUE-ANCHOR expansion — pulled back into a recall
    /// because it shares an entity with a live hit. The link-free sibling of <see cref="GraphNeighbor"/>:
    /// no authored edge is involved, so a memory can leak through it that the graph probe would clear.</summary>
    EntityNeighbor,
    DuplicateDetection,

    /// <summary>A live memory whose provenance was never established — carries the import trust floor
    /// until the nightly stage repairs it from a recognizable <c>Source</c> or an echo lifts it.</summary>
    UnknownProvenance,

    /// <summary>A live memory whose content no longer re-derives its own id: rewritten in place rather
    /// than superseded. The tamper signal.</summary>
    BrokenCommitment,

    /// <summary>A live memory citing a <c>DerivedFrom</c> target that no longer exists at all.</summary>
    DanglingCitation,

    /// <summary>A live memory citing a target whose content was amended (redacted) after the citation was
    /// made — the citation resolves, but not to the text it describes.</summary>
    AmendedCitation,
}

/// <summary>A single integrity failure: which memory, which check caught it, and the evidence.</summary>
public readonly record struct IntegrityFinding(
    string MemoryId, IntegrityCheck Check, string RepoId, string Evidence, bool ProbeFailed = false)
{
    /// <summary>
    /// A check that did not COMPLETE — categorically different from a memory that failed one, and the two
    /// must never share a row. No memory is implicated (hence the empty <see cref="MemoryId"/>): the probe
    /// itself broke, so this check's verdict is unknown for the entire sample. Rendering it as a data
    /// defect invents failures that were never observed; rendering it as clean hides that coverage
    /// narrowed. Consumers report it as absent coverage.
    /// </summary>
    public static IntegrityFinding ProbeFailure(IntegrityCheck check, string repoId, string reason) =>
        new("", check, repoId, $"check did not complete: {reason}", ProbeFailed: true);
}

/// <summary>The outcome of one <see cref="IIntegrityAuditor.VerifyAsync"/> run.</summary>
public sealed record IntegrityReport(string RepoId, DateTime RanAt, int MemoriesProbed, IReadOnlyList<IntegrityFinding> Findings)
{
    public bool Clean => Findings.Count == 0;

    /// <summary>
    /// The distinct checks actually exercised this run. Surfaced so the coverage test can pin that the
    /// auditor dispatched a probe for every <see cref="IntegrityCheck"/> value — the guard that a future
    /// check can't silently narrow the guarantee. A check that threw is absent, so a persistently failing
    /// probe is visible rather than mistaken for a passing one.
    /// </summary>
    public IReadOnlyList<IntegrityCheck> ChecksProbed { get; init; } = [];
}

/// <summary>
/// Deep module: enumerates every integrity check internally and asserts each invariant against live
/// production data — the runtime half of the FAMA forget guarantee (the CI half ships as
/// <c>FamaForgetTests</c>), widened in #80 to the trust claims that were previously asserted at write time
/// and never verified at read time. The single home for the "is this memory's claim about itself still
/// true?" invariant. Catches failure modes a fixture test structurally can't: a stale index that never
/// refreshed after a forget, a corrupted <c>ValidUntil</c>/<c>IsLatest</c>, a read path added later without
/// the filter, content patched directly in the database, or a citation whose source has since been erased.
///
/// READ-ONLY by contract — it holds a raw store and never mutates a memory. Repair belongs to the
/// maintenance stage, which writes through the bulk-write scope so the recall cache is invalidated;
/// mutating from here would leave recall serving scores computed from pre-repair provenance.
/// </summary>
public interface IIntegrityAuditor
{
    Task<IntegrityReport> VerifyAsync(string repoId, CancellationToken ct = default);
}
