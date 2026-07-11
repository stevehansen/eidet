namespace Eidet.Bench;

/// <summary>
/// The control arm: no memory at all. Every recall is empty, seeding and feedback are no-ops.
/// The delta between this arm and a memory backend is the lift memory provides.
/// </summary>
public sealed class NoMemoryBackend : IMemoryBackend
{
    public string Name => "no-memory";
    public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SeedTrajectoryAsync(SolveOutcome trajectory, CancellationToken ct = default) => Task.CompletedTask;
    public Task<RecalledContext> RecallAsync(SweTask task, CancellationToken ct = default) =>
        Task.FromResult(RecalledContext.Empty(task.InstanceId));
    public Task FeedbackAsync(RecalledContext used, bool wasUseful, CancellationToken ct = default) =>
        Task.CompletedTask;
}
