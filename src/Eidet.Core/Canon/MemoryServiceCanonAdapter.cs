using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Canon;

/// <summary>
/// Prod <see cref="ICanonMintPort"/>: mints an approved Canon draft into a real <see cref="MemoryEntry"/>
/// through the full <see cref="MemoryService.StoreAsync(StoreOptions, CancellationToken)"/> gate. This is
/// the single enforcement point for the zero-LLM write-path invariant — synthesized prose reaches
/// <c>memories/*</c> only here, only on Approve, and only through the standard secret+signal gate.
///
/// The payload is assembled from the draft's live members, which is why this adapter reads
/// <see cref="IEidetStore"/> (the port signature carries no member list): provenance follows the
/// anti-laundering rule over the surviving contributors, and the page inherits their defining tags.
/// Members that were forgotten between draft and approve are simply skipped — a page can still be
/// approved on a partial citation set; the full snapshot is preserved in <c>DerivedFrom</c> regardless.
/// </summary>
public sealed class MemoryServiceCanonAdapter : ICanonMintPort
{
    private const string CanonSource = "canon-review";
    private const float DefaultImportance = 0.7f;

    private readonly MemoryService _memory;
    private readonly IEidetStore _store;

    public MemoryServiceCanonAdapter(MemoryService memory, IEidetStore store)
    {
        _memory = memory;
        _store = store;
    }

    public async Task<CanonMintResult> MintAsync(CanonDraft draft, string? editedContent, CancellationToken ct = default)
    {
        var members = new List<MemoryEntry>();
        foreach (var id in draft.MemberIds)
        {
            var m = await _store.GetAsync(id, ct);
            if (m is not null) members.Add(m);
        }

        var content = string.IsNullOrWhiteSpace(editedContent) ? draft.ProposedContent : editedContent;

        // The page's own canon:* tag, plus the member-defining tags (union of member tags, minus any
        // canon:* so a page never re-tags itself by another page's tag).
        var canonTag = draft.Kind == CanonKind.Term
            ? CanonTags.Term(draft.Slug)
            : CanonTags.Domain(draft.Slug);
        var tags = new List<string> { canonTag };
        tags.AddRange(members
            .SelectMany(m => m.Tags)
            .Where(t => !t.StartsWith(CanonTags.Prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        // Importance follows the strongest contributor (clamped to a canon floor), or a sensible default
        // for authored terms that cite no memory.
        var importance = members.Count > 0
            ? Math.Clamp(members.Max(m => m.Importance), DefaultImportance, 1.0f)
            : DefaultImportance;

        var result = await _memory.StoreAsync(new StoreOptions(draft.RepoId, content, MemoryType.Insight)
        {
            Tags = tags,
            Importance = importance,
            Source = CanonSource,
            Provenance = ProvenanceRules.ForContributors(members),
            DerivedFrom = draft.MemberIds,          // full snapshot lineage, even where a member was forgotten
            Supersedes = draft.SupersedesCanonId,   // supersede the prior approved page when re-approving a slug
        }, ct);

        // A near-duplicate means the knowledge already lives in the store — treat the existing memory as
        // the mint target rather than failing the approve (mirrors the promotion adapter).
        if (result.DuplicateId is not null)
            return new CanonMintResult(true, result.DuplicateId, null);

        if (!result.Success)
            return new CanonMintResult(false, null, result.Reason ?? "canon mint rejected at the memory gate");

        return new CanonMintResult(true, result.Id, null);
    }
}
