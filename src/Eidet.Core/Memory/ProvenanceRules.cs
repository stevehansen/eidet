using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Anti-laundering provenance derivation for a memory synthesized from multiple contributors
/// (consolidation insights, Canon pages). An emission is born fully trusted
/// (<see cref="MemoryProvenance.Consolidation"/>) ONLY when every contributor is trusted; a single
/// untrusted (Pack/Intake) contributor demotes the emission to the least-trusted contributor's
/// provenance so <see cref="MemoryTrust"/> keeps demoting it at recall. This stops an attacker from
/// laundering a poisoned low-trust memory into a fully trusted synthesis ("compression-amplified toxin").
/// An empty contributor set vacuously earns <see cref="MemoryProvenance.Consolidation"/>.
/// </summary>
public static class ProvenanceRules
{
    public static MemoryProvenance ForContributors(IReadOnlyList<MemoryEntry> contributors) =>
        contributors.Any(c => !IsTrusted(c))
            ? contributors.OrderBy(c => MemoryTrust.ProvenanceTrust(c.Provenance)).First().Provenance
            : MemoryProvenance.Consolidation;

    /// <summary>A contributor is trusted unless its origin is a known poisoning surface (Pack/Intake),
    /// i.e. its provenance trust floor is the full 1.0.</summary>
    public static bool IsTrusted(MemoryEntry contributor) =>
        MemoryTrust.ProvenanceTrust(contributor.Provenance) >= 1.0;
}
