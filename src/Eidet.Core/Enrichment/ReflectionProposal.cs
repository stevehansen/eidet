using Eidet.Core.Domain;
using Eidet.Core.LooseEnds;

namespace Eidet.Core.Enrichment;

/// <summary>
/// One net-new memory the Reflector model proposes from feedback residue. The model returns ONLY
/// advisory content fields — <see cref="MemoryEntry.Importance"/>, <see cref="MemoryEntry.Provenance"/>,
/// <see cref="MemoryEntry.DerivedFrom"/>, and <see cref="MemoryEntry.Confidence"/> are engine-owned and
/// stamped after the fact, so a model can never launder itself into a trusted, high-importance lineage.
/// </summary>
public sealed record ReflectionProposal(
    string Content, MemoryType Type, Valence Valence, IReadOnlyList<string> Tags);

/// <summary>
/// The positive-signal residue the engine assembles for one repo and hands to the model: net-echoed
/// memories, Done/unpromoted loose ends, and Contradicted drift verdicts. Carries no scores or
/// provenance the model could act on — just the source content the reflection is derived from.
/// </summary>
public sealed record ReflectionResidue(
    string RepoId,
    IReadOnlyList<MemoryEntry> EchoedMemories,
    IReadOnlyList<LooseEnd> ResolvedEnds,
    IReadOnlyList<MemoryEntry> Contradicted)
{
    public bool IsEmpty => EchoedMemories.Count == 0 && ResolvedEnds.Count == 0 && Contradicted.Count == 0;
}
