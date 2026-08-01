using System.Security.Cryptography;
using System.Text;

namespace Eidet.Core.Domain;

/// <summary>
/// The single home for the memory-id format — including how to recognize this generator's own output.
///
/// A memory id embeds a truncated SHA256 over the memory's own content, so the id IS that memory's
/// content commitment (verified via <see cref="Matches"/> by <c>Eidet.Core.Memory.MemoryCommitment</c>).
/// That makes each preimage a FROZEN PERSISTED FORMAT, not an implementation detail: every id ever
/// minted is stored, and the read path re-derives it to detect content rewritten out from under a live
/// id. Changing the inputs, their order, or their rendering invalidates every existing id at once and
/// de-boosts the whole corpus SILENTLY at recall rather than failing a build. Treat these like wire
/// contracts; the golden-value tests are the guard.
///
/// There are TWO minting conventions, and callers do not get to invent a third — a hand-rolled id that
/// merely LOOKS like one of these (same 12-hex shape, different preimage) reads as tampered content.
/// <see cref="Matches"/> is the only correct way to ask "could this id have come from here?", so the
/// count of conventions stays private to this class.
/// </summary>
public static class MemoryIdGenerator
{
    /// <summary>
    /// Mints a timestamped id: the default convention, unique per (content, instant) so a re-store of
    /// identical content mints a distinct document rather than colliding with the original.
    ///
    /// <see cref="DateTime.Kind"/> is normalized here rather than at the call sites so the writer and
    /// the verifier agree by construction: <c>ToString("O")</c> renders a "Z" suffix for
    /// <see cref="DateTimeKind.Utc"/> and none for <see cref="DateTimeKind.Unspecified"/>, so a
    /// serializer round trip that dropped Kind would otherwise change the hash and report the entire
    /// corpus as tampered. Every existing call site passes Kind=Utc, so this is a no-op for every
    /// already-minted id (measured, not assumed — see <c>MemoryIdCommitmentConformanceTests</c>).
    /// </summary>
    public static string Generate(string repoId, MemoryType type, string content, DateTime createdAt) =>
        Build(repoId, type, TimestampedPreimage(content, createdAt));

    /// <summary>
    /// Mints a content-addressed id: a pure function of content, with NO timestamp, so identical content
    /// always yields the same id. Intake depends on this — it probes for a duplicate with a single
    /// <c>GetAsync</c> by id instead of running a similarity query, which is what makes re-ingesting an
    /// unchanged file cheap. Do not "unify" this with <see cref="Generate"/>: that would silently break
    /// duplicate-skip on every re-import.
    /// </summary>
    public static string GenerateContentAddressed(string repoId, MemoryType type, string content) =>
        Build(repoId, type, content);

    /// <summary>
    /// True when <paramref name="id"/> is one this generator could have minted for the supplied fields,
    /// under EITHER convention. The commitment check asks this rather than recomputing a single preimage,
    /// so a content-addressed memory is not mistaken for tampered content.
    ///
    /// Accepting two preimages does not weaken tamper detection: rewriting content changes the hash under
    /// BOTH, so rewritten content still matches neither. (This is not the rejected "candidate set of Kind
    /// renderings" hedge — that accepted several renderings of ONE preimage on a premise that turned out
    /// to be false. These are two distinct conventions genuinely in use.)
    /// </summary>
    public static bool Matches(string id, string repoId, MemoryType type, string content, DateTime createdAt) =>
        string.Equals(id, Generate(repoId, type, content, createdAt), StringComparison.Ordinal) ||
        string.Equals(id, GenerateContentAddressed(repoId, type, content), StringComparison.Ordinal);

    private static string TimestampedPreimage(string content, DateTime createdAt)
    {
        var utc = createdAt.Kind == DateTimeKind.Local
            ? createdAt.ToUniversalTime()
            : DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);
        return content + utc.ToString("O");
    }

    private static string Build(string repoId, MemoryType type, string preimage)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage));
        var shortHash = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"memories/{repoId}/{type.ToString().ToLowerInvariant()}/{shortHash}";
    }
}
