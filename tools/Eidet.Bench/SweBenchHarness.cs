using Eidet.Core.Benchmark;

namespace Eidet.Bench;

/// <summary>
/// Composes the four external ports into the SWE Context Bench run loop, mirroring the paper's
/// two phases (arXiv:2602.08316):
/// <list type="number">
/// <item><b>Ingestion</b> — related tasks are solved without memory and their trajectories
/// seeded into the backend.</item>
/// <item><b>Evaluation</b> — each base task recalls context, the solver attempts a patch, the
/// oracle rules FAIL_TO_PASS + PASS_TO_PASS, and recall feedback is reported.</item>
/// </list>
/// Resolution rate is over base tasks only; solver tokens are counted over base attempts only.
/// </summary>
public sealed class SweBenchHarness(
    ISweDatasetPort dataset,
    IMemoryBackend memory,
    ISolverPort solver,
    IOraclePort oracle,
    IReadOnlyList<ICapabilityScorer> scorers,
    TimeProvider clock)
{
    public async Task<SweBenchReport> RunAsync(int limit, CancellationToken ct = default)
    {
        if (!dataset.IsAvailable)
            throw new InvalidOperationException($"Dataset '{dataset.Name}' is not available.");
        if (!solver.IsAvailable)
            throw new InvalidOperationException("Solver is not available.");

        var tasks = await dataset.LoadAsync(limit, ct);
        var start = clock.GetTimestamp();
        await memory.ResetAsync(ct);

        var outcomes = new List<SolveOutcome>();

        foreach (var task in tasks.Where(t => t.IsRelated))
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await SolveAsync(task, RecalledContext.Empty(task.InstanceId), ct);
            await memory.SeedTrajectoryAsync(outcome, ct);
            outcomes.Add(outcome);
        }

        var baseCount = 0;
        var resolved = 0;
        long solveTokens = 0;
        foreach (var task in tasks.Where(t => !t.IsRelated))
        {
            ct.ThrowIfCancellationRequested();
            var context = await memory.RecallAsync(task, ct);
            var outcome = await SolveAsync(task, context, ct);
            if (!context.IsEmpty)
                await memory.FeedbackAsync(context, outcome.Verdict.Resolved, ct);
            outcomes.Add(outcome);

            baseCount++;
            solveTokens += outcome.Result.TokensUsed;
            if (outcome.Verdict.Resolved)
                resolved++;
        }

        var ama = scorers.Select(s => s.Score(outcomes)).ToList();
        return new SweBenchReport(
            dataset.Name, dataset.IsRealDataset, memory.Name,
            RelatedTasks: outcomes.Count - baseCount,
            BaseTasks: baseCount,
            Resolved: resolved,
            SolveTokens: solveTokens,
            Runtime: clock.GetElapsedTime(start),
            Ama: ama);
    }

    private async Task<SolveOutcome> SolveAsync(SweTask task, RecalledContext context, CancellationToken ct)
    {
        var request = new SolveRequest(
            task.InstanceId, task.Repo, task.BaseCommit, task.ProblemStatement,
            context.Fragments.Select(f => f.Content).ToList());
        var result = await solver.AttemptAsync(request, ct);
        var verdict = await oracle.ResolveAsync(task, result.Patch, ct);
        return new SolveOutcome(task, context, result, verdict);
    }
}
