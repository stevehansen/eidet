namespace Eidet.Bench;

/// <summary>One recalled memory: its backend id (for feedback attribution) and plain content.</summary>
public sealed record RecalledFragment(string MemoryId, string Content);

/// <summary>
/// The context a memory backend surfaced for one task. Deliberately plain strings — the seam
/// must stay expressible over MCP (the paper's competitor frameworks plug in as MCP tools, and
/// Eidet's own production adapter will drive <c>eidet_store</c>/<c>eidet_recall</c> in Phase 1).
/// </summary>
public sealed record RecalledContext(string TaskInstanceId, IReadOnlyList<RecalledFragment> Fragments)
{
    public static RecalledContext Empty(string taskInstanceId) => new(taskInstanceId, []);
    public bool IsEmpty => Fragments.Count == 0;
}

/// <summary>
/// The head-to-head seam: Eidet, a competitor framework, or the no-memory control arm all sit
/// behind this. Per the paper's methodology, seeding ingests a completed solve <em>trajectory</em>
/// (what was tried, the patch, whether it resolved) — not curated lessons.
/// </summary>
public interface IMemoryBackend
{
    string Name { get; }

    /// <summary>Clears the backend so a run starts from an empty memory. Called once per run.</summary>
    Task ResetAsync(CancellationToken ct = default);

    /// <summary>Ingests a completed solve trajectory (the paper's ingestion phase, related tasks).</summary>
    Task SeedTrajectoryAsync(SolveOutcome trajectory, CancellationToken ct = default);

    /// <summary>Recalls context for a base task before the solver attempts it.</summary>
    Task<RecalledContext> RecallAsync(SweTask task, CancellationToken ct = default);

    /// <summary>Reports whether the recalled context helped (task resolved) or misled.</summary>
    Task FeedbackAsync(RecalledContext used, bool wasUseful, CancellationToken ct = default);
}
