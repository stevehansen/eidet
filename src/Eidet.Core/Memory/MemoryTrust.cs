using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Derived, never-stored trust factor for a memory — the deterministic anti-poisoning gate.
/// A memory's trust is recomputed on every recall from its provenance, type, and earned feedback;
/// there is no stored trust field to forge, lie about, or let drift out of sync with the evidence.
///
/// The gate exists because the most dangerous memories are the cheapest to inject: a single
/// imported pack entry (MemoryGraft-style single-shot poisoning) or a wrongly-recalled
/// Procedure/Heuristic is net-negative even before it is echoed. So both the import surface
/// (Intake/Pack) and the action-shaped types (Procedure/Heuristic) start PROVISIONAL — their
/// retrieval weight is held below full trust — and only earned echoes (minus fizzles) lift them
/// back toward 1.0. Untainted first-party knowledge (UserStated/AgentInferred/ToolOutput/System
/// observations and insights) is fully trusted from the start, so this never penalizes the
/// honest path.
/// </summary>
public static class MemoryTrust
{
    /// <summary>Smoothing constant for the echo-lift term — keeps a memory provisional until it has
    /// accumulated several echoes, so a single feedback event cannot flip it to full trust.</summary>
    private const double EchoSmoothing = 3.0;

    /// <summary>Trust floor for the import / poisoning surface (Intake, Pack); 1.0 for everything else.</summary>
    private const double ImportTrust = 0.5;

    /// <summary>Trust floor for action-shaped types (Procedure, Heuristic); 1.0 for Insight/Observation.</summary>
    private const double ActionTypeTrust = 0.7;

    /// <summary>
    /// Trust factor in (0, 1.0], where 1.0 means full trust (no retrieval penalty). Starts at the
    /// lower of the provenance and type floors, then lets earned echoes lift it toward 1.0:
    /// <c>trust = base + (1 - base) · echo/(echo + fizzle + K)</c>.
    /// </summary>
    public static double Factor(MemoryEntry entry)
    {
        var floor = Math.Min(ProvenanceTrust(entry.Provenance), TypeTrust(entry.Type));
        var lift = entry.EchoCount / (double)(entry.EchoCount + entry.FizzleCount + EchoSmoothing);
        return floor + (1 - floor) * lift;
    }

    /// <summary>
    /// Provenance trust floor. Intake and Pack are the import / poisoning surface and stay
    /// provisional (0.5); first-party origins (UserStated, AgentInferred, ToolOutput, Consolidation,
    /// System) are fully trusted. Public so consolidation can identify untrusted contributing sources.
    /// </summary>
    public static double ProvenanceTrust(MemoryProvenance provenance) => provenance switch
    {
        MemoryProvenance.Intake or MemoryProvenance.Pack => ImportTrust,
        _ => 1.0,
    };

    /// <summary>
    /// Type trust floor. Procedure and Heuristic are action-shaped — a wrongly-recalled one is
    /// net-negative (SWE-Skills-Bench) — so they stay provisional (0.7) until echoed; Insight and
    /// Observation are fully trusted.
    /// </summary>
    private static double TypeTrust(MemoryType type) => type switch
    {
        MemoryType.Procedure or MemoryType.Heuristic => ActionTypeTrust,
        _ => 1.0,
    };
}
