using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Maps the free-form <c>source</c> tag stored on each memory back to the typed
/// <see cref="MemoryProvenance"/> enum used by retrieval and quality scoring.
/// "bundle" is a legacy alias for "pack" kept for older clients.
///
/// A source this build does not recognize yields <see cref="MemoryProvenance.Unknown"/>, NOT
/// <see cref="MemoryProvenance.AgentInferred"/>: an arbitrary <c>source</c> string on a store must not
/// be able to mint the same full recall trust as a vouched-for agent claim (#80, STRIDE T-20). The
/// nightly integrity stage repairs such a memory the moment its source becomes recognizable.
/// </summary>
public static class ProvenanceResolver
{
    // A table rather than a switch so <see cref="RecognizedSources"/> cannot drift from what
    // <see cref="FromSource"/> actually maps: the nightly repair queries the database for exactly this
    // set, and a source recognized here but missing from that query would never be repaired.
    // Ordinal, matching the case-sensitive string switch this replaced.
    private static readonly Dictionary<string, MemoryProvenance> BySource = new(StringComparer.Ordinal)
    {
        ["user"] = MemoryProvenance.UserStated,
        ["claude-session"] = MemoryProvenance.AgentInferred,
        ["consolidation"] = MemoryProvenance.Consolidation,
        ["intake"] = MemoryProvenance.Intake,
        ["pack"] = MemoryProvenance.Pack,
        ["bundle"] = MemoryProvenance.Pack,
        ["system"] = MemoryProvenance.System,
    };

    // Null-tolerant: the string switch this replaced accepted null and answered Unknown, and a deserialized
    // `source: null` reaches here despite the non-nullable model. A dictionary lookup would throw instead —
    // the same "we don't know" input crashing the write path rather than taking the safe default.
    public static MemoryProvenance FromSource(string source) =>
        source is not null && BySource.TryGetValue(source, out var provenance)
            ? provenance
            : MemoryProvenance.Unknown;

    /// <summary>
    /// Every <c>source</c> string this build can derive a provenance from — i.e. exactly the memories
    /// the nightly repair can act on. Used to scope that repair's backlog query: a memory whose source
    /// this build cannot map is unrepairable, and including it would let it occupy the head of the
    /// oldest-first queue forever and starve the ones that can be fixed.
    /// </summary>
    public static IReadOnlyCollection<string> RecognizedSources => BySource.Keys;
}
