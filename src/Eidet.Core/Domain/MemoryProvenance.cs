using Newtonsoft.Json;

namespace Eidet.Core.Domain;

[JsonConverter(typeof(MemoryProvenanceJsonConverter))]
public enum MemoryProvenance
{
    UserStated,
    AgentInferred,
    ToolOutput,
    Consolidation,
    Intake,
    Pack,
    System,
    Reflection,

    /// <summary>
    /// Provenance could not be established: the document predates the field, the stored value did not
    /// parse, or the write named a <c>source</c> this build does not recognize. Distinct from
    /// <see cref="AgentInferred"/> — "we don't know" is not "an agent vouched for it", and conflating
    /// the two made the safe default the insecure one (#80, STRIDE T-20).
    ///
    /// APPENDED LAST (int 8) on purpose: a legacy document that stored provenance as an integer is read
    /// back by ordinal, so inserting this value anywhere earlier would silently re-label every existing
    /// memory. It is also never emitted to a pack — see <c>MarkdownPackFormat</c> — so a foreign install
    /// applies its own default rather than inheriting our failure to establish one.
    /// Trusted no more than an imported memory; see <c>MemoryTrust.ProvenanceTrust</c>.
    /// </summary>
    Unknown,
}
