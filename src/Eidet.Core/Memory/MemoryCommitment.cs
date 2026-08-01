using System.Text.RegularExpressions;
using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>Whether a memory's content still matches the commitment its id makes over that content.</summary>
public enum CommitmentStatus
{
    /// <summary>Content re-derives the stored id — or the id was never minted as a commitment.</summary>
    Intact,

    /// <summary>
    /// Content was deliberately replaced by a record OF its own replacement (a redaction tombstone).
    /// A legitimate in-place mutation: the id stays stable so the lineage chain remains walkable.
    /// </summary>
    Amended,

    /// <summary>
    /// Content changed under a live id with nothing standing in its place — rewritten rather than
    /// superseded. The tamper signal.
    /// </summary>
    Broken,
}

/// <summary>
/// The memory id IS the verifier. <c>MemoryIdGenerator</c> derives every id from a truncated SHA256 over
/// the memory's own content, and the ordinary correction path — supersession — mints a NEW id, so content
/// that no longer re-derives its own live id was rewritten in place. This class is the single home for
/// that check; no separate attestation is stored, so there is nothing to forge alongside the content.
///
/// Which preimages count as "re-derives" is <c>MemoryIdGenerator.Matches</c>'s business, not this class's:
/// there is more than one minting convention (intake ids are content-addressed and carry no timestamp),
/// and hard-coding one of them here is exactly how the whole intake corpus once read as tampered.
///
/// Redaction is the one sanctioned in-place rewrite (it must keep the id so the chain stays walkable,
/// STRIDE T-15), and it is discriminated STRUCTURALLY — never by verb name. <see cref="IsAmendment"/>
/// recognizes the canonical amendment shape, and that shape carries no knowledge: forging it destroys
/// the very payload an attacker would want to inject, so the authorization is self-defeating. Any future
/// in-place mutation verb authorizes itself simply by rendering its content through <see cref="Render"/>.
///
/// The check is pure and cheap (one SHA256, one string compare), which is what lets it sit inside
/// <see cref="MemoryTrust"/> on the recall path.
/// </summary>
public static partial class MemoryCommitment
{
    /// <summary>Length of the hex hash segment <c>MemoryIdGenerator</c> appends to every id it mints.</summary>
    private const int HashSegmentLength = 12;

    /// <summary>
    /// The amendment shape: a whole-content, single-line <c>[verb: reason @ when]</c> record. Deliberately
    /// tolerant of the timestamp portion rather than parsing a strict round-trip ("O") date — tombstones
    /// written across the corpus's history use different renderings, and a stricter match would reclassify
    /// the older ones as tampering.
    /// </summary>
    [GeneratedRegex(@"^\[[a-z]+: .+ @ [^\]]+\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex AmendmentShape();

    /// <summary>
    /// Verifies <paramref name="entry"/>'s content against the commitment in its own id.
    /// An id that <c>MemoryIdGenerator</c> did not mint (a hand-built fixture, a foreign row) carries no
    /// commitment to break and reports <see cref="CommitmentStatus.Intact"/> — the check must never turn a
    /// non-conforming id into a corpus-wide de-boost, and the threat it defends against (content patched
    /// under a preserved id) always leaves the id canonical.
    /// </summary>
    public static CommitmentStatus Check(MemoryEntry entry)
    {
        if (!IsMintedId(entry.Id)) return CommitmentStatus.Intact;

        if (MemoryIdGenerator.Matches(entry.Id, entry.RepoId, entry.Type, entry.Content, entry.CreatedAt))
            return CommitmentStatus.Intact;

        return IsAmendment(entry.Content) ? CommitmentStatus.Amended : CommitmentStatus.Broken;
    }

    /// <summary>
    /// Renders the canonical amendment content for an in-place mutation verb ("redacted", "erased", …).
    /// The single definition of the shape <see cref="IsAmendment"/> recognizes — a verb that inlines its
    /// own format instead of calling this reads as <see cref="CommitmentStatus.Broken"/>.
    /// </summary>
    public static string Render(string verb, string reason, DateTime atUtc) =>
        $"[{verb}: {reason} @ {atUtc:O}]";

    /// <summary>True when <paramref name="content"/> is a record of its own replacement rather than knowledge.</summary>
    public static bool IsAmendment(string content) =>
        !string.IsNullOrEmpty(content) && AmendmentShape().IsMatch(content);

    private static bool IsMintedId(string id)
    {
        if (!id.StartsWith("memories/", StringComparison.Ordinal)) return false;

        var lastSlash = id.LastIndexOf('/');
        var hash = id.AsSpan(lastSlash + 1);
        if (hash.Length != HashSegmentLength) return false;

        foreach (var c in hash)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        }
        return true;
    }
}
