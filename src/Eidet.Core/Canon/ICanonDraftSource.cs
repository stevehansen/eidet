namespace Eidet.Core.Canon;

/// <summary>
/// A pluggable draft generator — the extension seam for new Canon kinds/sources. New sources register
/// additively into <see cref="CanonService"/>'s source list; the regeneration orchestrator never changes.
/// P1 ships <c>EntityAggregationDraftSource</c> (over <c>IEidetStore</c>) and
/// <c>UbiquitousLanguageDraftSource</c> (over UL.md); P2 adds a tag-cluster Domain source. Sources own
/// their own reads (store, file) — they are NOT lifted to service-level read ports.
/// </summary>
public interface ICanonDraftSource
{
    /// <summary>Stable identifier — the draft's <see cref="CanonDraft.SourceName"/> and the bulk-approve filter key.</summary>
    string Name { get; }

    /// <summary>Whether this source can produce candidates for the given context (e.g. UL.md exists).</summary>
    bool AppliesTo(CanonProposalContext ctx);

    /// <summary>Stream the candidate drafts this source proposes for the context.</summary>
    IAsyncEnumerable<CanonDraftCandidate> ProposeAsync(CanonProposalContext ctx, CancellationToken ct = default);
}

/// <summary>
/// The regeneration context. <see cref="RepoId"/> is the normalized repo id (memory namespace);
/// <see cref="ProjectPath"/> is the raw filesystem path (for file-backed sources like UL.md).
/// </summary>
public sealed record CanonProposalContext(string RepoId, string ProjectPath);

/// <summary>A single proposed draft, before the damper decides whether to store/refresh/skip it.</summary>
public sealed record CanonDraftCandidate(
    CanonKind Kind, string Slug, string Title, string ProposedContent,
    IReadOnlyList<string> MemberIds, string Fingerprint);
