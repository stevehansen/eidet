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
/// (Pack/Reflection) and the action-shaped types (Procedure/Heuristic) start PROVISIONAL — their
/// retrieval weight is held below full trust — and only earned echoes (minus fizzles) lift them
/// back toward 1.0. Untainted first-party knowledge (UserStated/AgentInferred/ToolOutput/System
/// observations and insights) is fully trusted from the start, so this never penalizes the
/// honest path.
///
/// Repo-file intake gets a THIRD tier of its own (<see cref="IntakeTrust"/>, 0.7) rather than sharing
/// the import floor. A de-boost is only free when the memory it holds down is redundant; when the
/// corpus is mostly intake, holding intake at the import floor makes the repo's own documentation
/// unreachable behind session chatter — measured, not theorized. The floor a memory sits on has to
/// track how it actually arrived, or the gate stops being a poisoning control and becomes a
/// relevance bug.
///
/// Two later hardenings (#80) close the gap between a trust CLAIM and a verified one. Provenance that
/// was never established (<see cref="MemoryProvenance.Unknown"/>) is treated as an import, not as a
/// vouched-for agent claim. And the commitment factor multiplies AFTER the echo lift, so echoes can
/// rehabilitate an unknown origin but can NEVER launder content that was rewritten out from under its
/// own id commitment — the only sanctioned repair for that is supersession, which mints a fresh id.
/// Getting that ordering backwards silently reopens the laundering hole (STRIDE T-8).
///
/// Trust is a DE-BOOST, never a cutoff: even a broken commitment stays recallable (#37
/// downrank-never-hide) and raises a Critical dashboard finding instead of disappearing.
/// </summary>
public static class MemoryTrust
{
    /// <summary>Smoothing constant for the echo-lift term — keeps a memory provisional until it has
    /// accumulated several echoes, so a single feedback event cannot flip it to full trust.</summary>
    private const double EchoSmoothing = 3.0;

    /// <summary>Trust floor for the import / poisoning surface (Pack, Reflection, Unknown); 1.0 for everything else.</summary>
    private const double ImportTrust = 0.5;

    /// <summary>
    /// Trust floor for repo-file intake — above the import floor, below first-party.
    ///
    /// Intake reads files that are ALREADY in the repo the agent is working in: the same bytes any
    /// tool call would read, from a tree the user controls. It is therefore not the remote-import
    /// surface <see cref="ImportTrust"/> exists to hold down — nobody smuggled a CLAUDE.md in over
    /// the wire. But it is still unvouched: a file asserting something is weaker evidence than an
    /// agent or a user asserting it, so intake does not reach 1.0 either.
    ///
    /// The value was measured, not picked. On an 87-repo field corpus (79% intake), a term appearing
    /// in exactly ONE live memory of a repo was found by the index every time and then ranked below
    /// memories that did not contain it at all, because the floor multiplies the whole fused score:
    /// hit@5 for intake-owned facts was 53% against 94% for agent-stored ones. Sweeping the floor
    /// over the same probes put the knee at 0.7 — hit@5 56% → 90%, median rank 5 → 2 — with 1.0
    /// buying almost nothing beyond it (91%). Below the knee the repo's own documentation is
    /// effectively unreachable; above it, nothing is gained but poisoning resistance is spent.
    ///
    /// Equal to <see cref="ActionTypeTrust"/> by coincidence, not by coupling: they answer different
    /// questions (who vouched for this vs. what happens if it is wrong) and may move apart.
    /// </summary>
    private const double IntakeTrust = 0.7;

    /// <summary>Trust floor for action-shaped types (Procedure, Heuristic); 1.0 for Insight/Observation.</summary>
    private const double ActionTypeTrust = 0.7;

    /// <summary>
    /// Multiplier for content that no longer matches its own id commitment — half the
    /// <see cref="ImportTrust"/> floor. Applied AFTER the echo lift, so it caps a tampered memory at
    /// this value no matter how many echoes it accumulated before the rewrite.
    /// </summary>
    private const double BrokenCommitmentTrust = 0.25;

    /// <summary>
    /// Trust factor in (0, 1.0], where 1.0 means full trust (no retrieval penalty). Starts at the
    /// lower of the provenance and type floors, lets earned echoes lift it toward 1.0
    /// (<c>base + (1 - base) · echo/(echo + fizzle + K)</c>), then multiplies by the content
    /// commitment factor.
    /// </summary>
    public static double Factor(MemoryEntry entry) => Explain(entry).Factor;

    /// <summary>
    /// The same computation as <see cref="Factor"/> with every term exposed — for forensics on a single
    /// memory ("why is this distrusted?"), not for the recall loop. <see cref="Factor"/> delegates here so
    /// there is exactly ONE definition of the algebra to keep correct.
    /// </summary>
    public static TrustBreakdown Explain(MemoryEntry entry)
    {
        var provenanceFloor = ProvenanceTrust(entry.Provenance);
        var typeFloor = TypeTrust(entry.Type);
        var floor = Math.Min(provenanceFloor, typeFloor);
        var lift = entry.EchoCount / (double)(entry.EchoCount + entry.FizzleCount + EchoSmoothing);
        var commitment = MemoryCommitment.Check(entry);
        var commitmentFactor = CommitmentTrust(commitment);
        return new TrustBreakdown(
            provenanceFloor, typeFloor, lift, commitment, commitmentFactor,
            (floor + (1 - floor) * lift) * commitmentFactor);
    }

    /// <summary>
    /// Provenance trust floor. Pack is the import / poisoning surface and stays provisional (0.5);
    /// Reflection is LLM-synthesized net-new content that must EARN trust via echoes rather than
    /// being born trusted, so it shares the same provisional floor. Intake sits above them on its
    /// own floor (0.7) because it reads local repo files rather than accepting remote bytes.
    /// First-party origins (UserStated, AgentInferred, ToolOutput, Consolidation, System) are fully
    /// trusted.
    /// Public so consolidation / reflection can identify untrusted contributing sources.
    ///
    /// Note what is NOT here: a fallback to full trust. The trusted origins are enumerated explicitly and
    /// everything else — including <see cref="MemoryProvenance.Unknown"/> and any undefined ordinal that
    /// slipped past the deserializer's closed-world guard — lands on the import floor. Removing the
    /// insecure default is stronger than guarding the one path that reached it (#80, STRIDE T-20).
    /// </summary>
    public static double ProvenanceTrust(MemoryProvenance provenance) => provenance switch
    {
        MemoryProvenance.UserStated or MemoryProvenance.AgentInferred or MemoryProvenance.ToolOutput
            or MemoryProvenance.Consolidation or MemoryProvenance.System => 1.0,
        // Repo-file intake sits on its own floor between the two: see IntakeTrust for why local files
        // are not the remote-import surface, and for the measurement that fixed the value.
        MemoryProvenance.Intake => IntakeTrust,
        MemoryProvenance.Pack or MemoryProvenance.Reflection => ImportTrust,
        // EXACTLY the import floor, never the intake floor: the pack-import clamp in MarkdownPackFormat
        // compares provenance floors, so lifting Unknown would let a pack declare its way past Pack.
        MemoryProvenance.Unknown => ImportTrust,
        _ => ImportTrust,
    };

    /// <summary>
    /// Content-commitment multiplier. An <see cref="CommitmentStatus.Amended"/> memory is NOT penalized —
    /// a redaction tombstone is a legitimate record that carries no knowledge to distrust — while a
    /// <see cref="CommitmentStatus.Broken"/> one is held at <see cref="BrokenCommitmentTrust"/>.
    /// </summary>
    private static double CommitmentTrust(CommitmentStatus status) => status switch
    {
        CommitmentStatus.Intact or CommitmentStatus.Amended => 1.0,
        CommitmentStatus.Broken => BrokenCommitmentTrust,
        _ => BrokenCommitmentTrust,
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

/// <summary>
/// Every term behind one memory's trust factor, for forensics. Rare surface — the recall path calls
/// <see cref="MemoryTrust.Factor"/>. <c>ProvenanceFloor</c>/<c>TypeFloor</c> combine as their minimum,
/// <c>EchoLift</c> raises that toward 1.0, and <c>CommitmentFactor</c> multiplies the result — the
/// ordering that stops echoes from laundering a broken commitment.
/// </summary>
public readonly record struct TrustBreakdown(
    double ProvenanceFloor,
    double TypeFloor,
    double EchoLift,
    CommitmentStatus Commitment,
    double CommitmentFactor,
    double Factor);
