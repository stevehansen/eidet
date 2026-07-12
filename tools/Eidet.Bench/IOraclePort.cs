namespace Eidet.Bench;

/// <summary>
/// SWE-bench-identical resolution verdict: a patch resolves a task only when the FAIL_TO_PASS
/// tests now pass AND the PASS_TO_PASS tests still pass. Kept as two explicit booleans so a
/// regression (P2P broken) is distinguishable from a non-fix (F2P still failing).
/// </summary>
public sealed record Verdict(bool FailToPassPassed, bool PassToPassPassed)
{
    public bool Resolved => FailToPassPassed && PassToPassPassed;
}

/// <summary>
/// External touch #3: execution-based resolution. The production adapter (Phase 1) reuses an
/// existing SWE-bench execution harness per the design; Phase 0 ships replay/recording adapters.
/// </summary>
public interface IOraclePort
{
    Task<Verdict> ResolveAsync(SweTask task, string patch, CancellationToken ct = default);
}
