using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Bench;

/// <summary>
/// Eidet as the memory backend, driven in-process through the real <see cref="MemoryService"/>
/// (write gates, fusion recall, feedback learning — the full production pipeline) over whatever
/// <see cref="IEidetStore"/> the composer supplies. The deterministic CI arm runs it over the
/// in-memory test store; the Phase 1 production adapter drives Eidet's MCP tools instead, to be
/// apples-to-apples with the published competitor rows.
/// </summary>
public sealed class InProcessEidetBackend(MemoryService memories, IEidetStore store, string repoId) : IMemoryBackend
{
    /// <summary>Top-k recalled fragments handed to the solver per task.</summary>
    private const int FragmentBudget = 3;

    public string Name => "eidet-inprocess";

    public async Task ResetAsync(CancellationToken ct = default)
    {
        // Collect every id by paging forward, THEN delete — so the loop can't depend on a delete
        // being reflected in the next browse (which need not hold for an async-indexed store).
        const int pageSize = 256;
        var ids = new List<string>();
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await store.BrowseAsync(repoId, skip, pageSize, ct: ct);
            ids.AddRange(page.Select(e => e.Id));
            if (page.Count < pageSize)
                break;
        }
        foreach (var id in ids)
            await store.HardDeleteAsync(id, ct);
    }

    public async Task SeedTrajectoryAsync(SolveOutcome trajectory, CancellationToken ct = default)
    {
        var task = trajectory.Task;
        var content =
            $"Solve trajectory for {task.InstanceId} ({task.Repo}): {task.ProblemStatement} " +
            $"Resolution {(trajectory.Verdict.Resolved ? "succeeded" : "failed")} " +
            $"(FAIL_TO_PASS {Passed(trajectory.Verdict.FailToPassPassed)}, PASS_TO_PASS {Passed(trajectory.Verdict.PassToPassPassed)}). " +
            $"Patch applied:\n{trajectory.Result.Patch}";

        var result = await memories.StoreAsync(
            repoId, content, MemoryType.Observation, tags: ["swe-bench", "trajectory"], ct: ct);
        // A silently dropped trajectory would fake a weaker memory arm — fail loudly instead.
        if (!result.Success)
            throw new InvalidOperationException(
                $"Trajectory for {task.InstanceId} was rejected by the write gate: {result.Reason}");
    }

    public async Task<RecalledContext> RecallAsync(SweTask task, CancellationToken ct = default)
    {
        var hits = await memories.RecallAsync(
            repoId,
            new RecallOptions(task.ProblemStatement) { Limit = FragmentBudget, CrossRepo = false },
            ct);
        return new RecalledContext(
            task.InstanceId,
            hits.Select(h => new RecalledFragment(h.Id, h.Content)).ToList());
    }

    public async Task FeedbackAsync(RecalledContext used, bool wasUseful, CancellationToken ct = default)
    {
        foreach (var fragment in used.Fragments)
            await memories.FeedbackAsync(fragment.MemoryId, wasUseful, ct: ct);
    }

    private static string Passed(bool value) => value ? "passed" : "failed";
}
