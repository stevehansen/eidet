using Eidet.Core.Domain;
using Eidet.Core.Text;

namespace Eidet.Core.Memory;

/// <summary>Evidence for a write-time contradiction: which high-trust incumbent the incoming memory
/// contradicts, the two stances, the content similarity, and the incumbent's derived trust.</summary>
public readonly record struct ConflictFinding(
    string ContradictedId, Valence Stance, Valence ContradictedStance, float Similarity, double ContradictedTrust);

/// <summary>
/// The single home of the write-time contradiction rule — structural and ZERO-LLM. Reuses the
/// near-duplicate neighbors already fetched on the write path (no new query). A conflict requires
/// ALL THREE of: near-duplicate content (established by the neighbor's membership in the top-k),
/// opposite hard valence signs (<see cref="ValencePolarity.Conflicts"/>), and the incumbent being
/// high-trust (<see cref="MemoryTrust.Factor"/> ≥ <paramref name="highTrust"/>). Neutral/Cautionary
/// incoming has sign 0, so it never conflicts — which naturally bounds false positives to explicit
/// opposite-stance pairs (the T-13 target). Exactly one structural signal exists today, so it is
/// inlined here rather than behind a pluggable rule registry.
/// </summary>
public static class ConflictGate
{
    public static ConflictFinding? Check(
        MemoryEntry incoming, IReadOnlyList<MemoryEntry> neighbors, double highTrust = 0.9)
    {
        if (ValencePolarity.Sign(incoming.Valence) == 0 || neighbors.Count == 0)
            return null;

        ConflictFinding? best = null;
        foreach (var incumbent in neighbors)
        {
            if (!ValencePolarity.Conflicts(incoming.Valence, incumbent.Valence))
                continue;
            var trust = MemoryTrust.Factor(incumbent);
            if (trust < highTrust)
                continue;

            // Prefer the most-trusted incumbent as the one on record — it is the strongest claim
            // the incoming memory is contradicting.
            if (best is { } b && trust <= b.ContradictedTrust)
                continue;

            best = new ConflictFinding(
                incumbent.Id, incoming.Valence, incumbent.Valence,
                WordSimilarity.Compute(incoming.Content, incumbent.Content), trust);
        }

        return best;
    }
}
