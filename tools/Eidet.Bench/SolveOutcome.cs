namespace Eidet.Bench;

/// <summary>
/// Everything that happened for one task: what was recalled, what the solver produced, and the
/// oracle's verdict. The harness accumulates these; capability scorers consume them; seeding a
/// related task ingests one as the trajectory.
/// </summary>
public sealed record SolveOutcome(
    SweTask Task,
    RecalledContext Context,
    SolveResult Result,
    Verdict Verdict);
