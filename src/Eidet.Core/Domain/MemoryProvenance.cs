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
}
