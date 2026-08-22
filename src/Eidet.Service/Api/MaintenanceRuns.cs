using Eidet.Service.Tools;

namespace Eidet.Service.Api;

/// <summary>
/// Handles for maintenance runs that outlive the request that started them. A full pass over a
/// large repo takes longer than any sensible client timeout, so
/// <see cref="Endpoints.MaintenanceEndpoints"/> waits a grace window and then hands back a run id;
/// the run keeps going and the caller polls it here.
///
/// Deliberately not keyed by repo, and deliberately not a place where work is deduplicated: one
/// pipeline execution per repo is
/// <see cref="Eidet.Core.Maintenance.CoalescingMaintenanceRunner"/>'s job, and it covers every
/// caller rather than only the REST ones. Two POSTs for the same repo therefore get two run ids
/// that report the same underlying pass — which is honest, because two requests were made.
/// </summary>
internal sealed class MaintenanceRuns
{
    /// <summary>
    /// How long a finished run stays pollable. Long enough for a caller that backed off to come
    /// back for its report, short enough that ids do not accumulate for the life of the service.
    /// </summary>
    private static readonly TimeSpan ResultTtl = TimeSpan.FromHours(1);

    private readonly Dictionary<string, MaintenanceRun> _runs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Starts <paramref name="work"/> and returns its handle. Pass the host lifetime token as
    /// <paramref name="ct"/>, never a per-request one, or the run dies with its response.
    /// </summary>
    public MaintenanceRun Start(string repoId, Func<CancellationToken, Task<ToolResult>> work, CancellationToken ct)
    {
        var run = MaintenanceRun.Start(repoId, work, ct);

        lock (_gate)
        {
            EvictExpired();
            _runs[run.Id] = run;
        }

        return run;
    }

    /// <summary>The run with this id, or null when it never existed or has aged out.</summary>
    public MaintenanceRun? Find(string runId)
    {
        lock (_gate) return _runs.GetValueOrDefault(runId);
    }

    /// <summary>Drops runs whose results have aged out. A run still going has no CompletedAt, and
    /// a null never compares below the cutoff, so it is never evicted from under its poller.</summary>
    private void EvictExpired()
    {
        var cutoff = DateTime.UtcNow - ResultTtl;
        foreach (var id in _runs.Where(r => r.Value.CompletedAt < cutoff).Select(r => r.Key).ToList())
            _runs.Remove(id);
    }
}

/// <summary>One maintenance run, addressable by <see cref="Id"/> for as long as the table keeps it.</summary>
internal sealed class MaintenanceRun
{
    private Task<ToolResult> _work = null!;

    private MaintenanceRun(string repoId)
    {
        Id = Guid.NewGuid().ToString("N");
        RepoId = repoId;
        StartedAt = DateTime.UtcNow;
    }

    public string Id { get; }
    public string RepoId { get; }
    public DateTime StartedAt { get; }
    public DateTime? CompletedAt { get; private set; }
    public bool IsRunning => !_work.IsCompleted;
    public Task<ToolResult> Work => _work;

    public static MaintenanceRun Start(
        string repoId, Func<CancellationToken, Task<ToolResult>> work, CancellationToken ct)
    {
        var run = new MaintenanceRun(repoId);
        run._work = run.ObserveAsync(work, ct);
        return run;
    }

    /// <summary>
    /// The result if the run finishes within <paramref name="grace"/>, otherwise null — leaving the
    /// run going. Host shutdown reads as "not yet" rather than as an error.
    /// </summary>
    public async Task<ToolResult?> WaitAsync(TimeSpan grace, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(grace, timer.Token);

        if (await Task.WhenAny(_work, delay) != _work) return null;

        await timer.CancelAsync(); // a fast run must not leave a 30s timer behind
        return await _work;
    }

    /// <summary>
    /// Produces a <see cref="ToolResult"/> in every case. A faulted task would have to be handled
    /// twice — once by the caller waiting out the grace window, once by a later poller — and a
    /// failure already has a ToolResult shape, so neither of them needs a catch.
    /// </summary>
    private async Task<ToolResult> ObserveAsync(Func<CancellationToken, Task<ToolResult>> work, CancellationToken ct)
    {
        try
        {
            return await work(ct);
        }
        catch (Exception ex)
        {
            return ToolResult.Internal($"Maintenance run failed ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            CompletedAt = DateTime.UtcNow;
        }
    }
}
