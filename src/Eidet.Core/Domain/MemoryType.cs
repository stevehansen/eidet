namespace Eidet.Core.Domain;

public enum MemoryType
{
    Observation,  // Raw facts, events, decisions from a session
    Insight,      // Consolidated/derived knowledge
    Procedure,    // How-to steps, workflows, patterns
    Heuristic,    // Do/don't lessons from experience
}
