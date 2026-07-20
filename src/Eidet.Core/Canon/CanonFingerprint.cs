using Eidet.Core.Domain;

namespace Eidet.Core.Canon;

/// <summary>
/// The draft's staleness fingerprint: a SHA256 over the render-relevant fields (kind, title, the content
/// basis) plus the ordered member-id set. Two candidates with the same fingerprint are the same draft —
/// the damper skips them; a changed fingerprint means the synthesis or its membership drifted and the
/// draft is refreshed (or a superseding draft is queued over an approved page).
/// </summary>
public static class CanonFingerprint
{
    // Unit separator — cannot occur in slugs/titles/content lines, so joins can't collide across fields.
    private const string Separator = "";

    public static string Of(CanonKind kind, string title, string contentBasis, IReadOnlyList<string> memberIds)
    {
        var parts = new List<string> { kind.ToString(), title, contentBasis };
        parts.AddRange(memberIds.OrderBy(id => id, StringComparer.Ordinal));
        return ContentHash.Of(string.Join(Separator, parts));
    }
}
