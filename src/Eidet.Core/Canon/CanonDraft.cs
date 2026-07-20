namespace Eidet.Core.Canon;

/// <summary>The two kinds of Canon page. Terms ship in P1; Domains follow in P2.</summary>
public enum CanonKind { Domain, Term }

/// <summary>
/// A Canon draft's lifecycle. <see cref="Approving"/> is the transient claim state (the LooseEnd
/// <c>Resolving</c> twin) — held only between a won approve-claim and the mint that finalizes it, and
/// never surfaced in REST responses. <see cref="Superseded"/> is vestigial for slug-keyed drafts (a
/// newer candidate for the same slug refreshes the one document in place) and reserved for later phases.
/// </summary>
public enum CanonDraftStatus { Pending, Approving, Approved, Rejected, Superseded }

/// <summary>
/// A proposed Canon page awaiting Operator review — synthesized (or UL-seeded) prose that is NOT yet a
/// <c>MemoryEntry</c>. Lives in its own <c>canondrafts/*</c> collection so no maintenance stage ever
/// enumerates it; Approve is the sole write path from here into <c>memories/*</c>, through the full gate.
/// ID: <c>canondrafts/{repoId}/{kind}/{slug}</c> — slug-keyed, so it is the damper anchor: one document
/// per (repo, kind, slug), refreshed in place rather than duplicated.
/// </summary>
public sealed class CanonDraft
{
    public string Id { get; set; } = "";
    public string RepoId { get; set; } = "";            // Local layer only; no LayerId
    public CanonKind Kind { get; set; }

    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string ProposedContent { get; set; } = "";   // synthesized prose; secret-scanned at draft time

    public List<string> MemberIds { get; set; } = [];   // the citation snapshot the synthesis derives from
    public string Fingerprint { get; set; } = "";       // SHA256 over the ordered member set + render fields

    public DateTimeOffset ProposedAt { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }   // set on rejection; blocks re-proposal until elapsed

    public CanonDraftStatus Status { get; set; } = CanonDraftStatus.Pending;
    public string? RejectReason { get; set; }
    public string? SupersedesCanonId { get; set; }       // the approved page's minted memory id, when superseding
    public string? MintedMemoryId { get; set; }          // set when Approve mints the canon page

    /// <summary>The <see cref="ICanonDraftSource.Name"/> that produced this draft — the filter key for
    /// <see cref="CanonService.BulkApproveAsync"/> (e.g. approve all UL-seeded terms at once).</summary>
    public string SourceName { get; set; } = "";
}
