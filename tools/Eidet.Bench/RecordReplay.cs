namespace Eidet.Bench;

/// <summary>Decorates a real solver, recording every result into the transcript it returns.</summary>
public sealed class RecordingSolver(ISolverPort inner, Transcript transcript) : ISolverPort
{
    public bool IsAvailable => inner.IsAvailable;

    public async Task<SolveResult> AttemptAsync(SolveRequest request, CancellationToken ct = default)
    {
        var result = await inner.AttemptAsync(request, ct);
        transcript.RecordSolve(request, result);
        return result;
    }

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
}

/// <summary>
/// Replays recorded solver results offline. A request that was never recorded throws instead of
/// fabricating — a stale transcript must fail loudly, never re-derive a wrong number silently.
/// </summary>
public sealed class ReplaySolver(Transcript transcript) : ISolverPort
{
    public bool IsAvailable => true;

    public Task<SolveResult> AttemptAsync(SolveRequest request, CancellationToken ct = default) =>
        transcript.FindSolve(request) is { } result
            ? Task.FromResult(result)
            : throw new InvalidOperationException(
                $"No transcript entry for solve request {Transcript.KeyForSolve(request)} " +
                $"(task {request.InstanceId}). The transcript is stale — re-record it " +
                "(EIDET_BENCH_WRITE=1 regenerates the fixture transcript).");

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>Decorates a real oracle, recording every verdict into the transcript.</summary>
public sealed class RecordingOracle(IOraclePort inner, Transcript transcript) : IOraclePort
{
    public async Task<Verdict> ResolveAsync(SweTask task, string patch, CancellationToken ct = default)
    {
        var verdict = await inner.ResolveAsync(task, patch, ct);
        transcript.RecordVerdict(task, patch, verdict);
        return verdict;
    }
}

/// <summary>Replays recorded verdicts offline; unknown (task, patch) pairs throw, never fabricate.</summary>
public sealed class ReplayOracle(Transcript transcript) : IOraclePort
{
    public Task<Verdict> ResolveAsync(SweTask task, string patch, CancellationToken ct = default) =>
        transcript.FindVerdict(task, patch) is { } verdict
            ? Task.FromResult(verdict)
            : throw new InvalidOperationException(
                $"No transcript entry for verdict {Transcript.KeyForVerdict(task.InstanceId, patch)} " +
                $"(task {task.InstanceId}). The transcript is stale — re-record it " +
                "(EIDET_BENCH_WRITE=1 regenerates the fixture transcript).");
}
