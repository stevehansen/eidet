using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Maps the free-form <c>source</c> tag stored on each memory back to the typed
/// <see cref="MemoryProvenance"/> enum used by retrieval and quality scoring.
/// "bundle" is a legacy alias for "pack" kept for older clients.
/// </summary>
public static class ProvenanceResolver
{
    public static MemoryProvenance FromSource(string source) => source switch
    {
        "user" => MemoryProvenance.UserStated,
        "claude-session" => MemoryProvenance.AgentInferred,
        "consolidation" => MemoryProvenance.Consolidation,
        "intake" => MemoryProvenance.Intake,
        "pack" or "bundle" => MemoryProvenance.Pack,
        "system" => MemoryProvenance.System,
        _ => MemoryProvenance.AgentInferred,
    };
}
