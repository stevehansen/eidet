using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance;

/// <summary>
/// One pipeline execution per repo at a time: a caller arriving while a run for the same repo is
/// still in flight is handed that run's report instead of starting a second pass.
///
/// The pipeline rewrites whole documents through a per-run <see cref="SharedEntryStore"/>, so two
/// concurrent passes over one repo interleave their writes and each reverts the other's field edits
/// — the same failure the shared store removes *within* a run. Nothing enforced this before because
/// the two service callers rarely overlapped: the REST caller blocked for the whole run, so an
/// overlap meant a hand-triggered run landing inside the scheduler's tick. A REST endpoint that
/// hands back a run id and lets the caller poll makes overlap ordinary — a caller that stops
/// waiting and retries would otherwise start a second pass over a repo already being rewritten.
///
/// Only the repo-path overload coalesces. A <see cref="MaintenanceRequest"/> carries a stage subset
/// and retention overrides, so two such calls are not interchangeable and each runs on its own;
/// both service callers (the scheduler and the maintenance tool handler) use the repo-path overload.
/// </summary>
public sealed class CoalescingMaintenanceRunner : IMaintenanceRunner
{
    private readonly IMaintenanceRunner _inner;

    /// <summary>
    /// Latest run per repo, completed ones included. An entry is replaced — never removed on
    /// completion — because removing it would open a window where a caller attaches to a run that
    /// has already produced its report and receives a stale one. Bounded by the repo count, and a
    /// report is a handful of stage outcomes.
    /// </summary>
    private readonly Dictionary<string, Task<MaintenanceReport>> _latest = new(StringComparer.Ordinal);

    private readonly object _gate = new();

    public CoalescingMaintenanceRunner(IMaintenanceRunner inner)
    {
        _inner = inner;
    }

    public Task<MaintenanceReport> RunAsync(string repoPathOrId, CancellationToken ct = default)
    {
        var repoId = RepoIdNormalizer.Normalize(repoPathOrId);

        lock (_gate)
        {
            if (_latest.TryGetValue(repoId, out var existing) && !existing.IsCompleted)
                return existing;

            // Started under the lock so a second caller cannot slip into the gap between starting
            // the run and publishing it. Safe to call inner here: it only starts the pipeline.
            var run = _inner.RunAsync(repoId, ct);
            _latest[repoId] = run;
            return run;
        }
    }

    public Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default) =>
        _inner.RunAsync(request, ct);
}
