using System.Diagnostics;
using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Eidet.Core.Services;

/// <summary>
/// Persisted task scheduler backed by RavenDB documents with the Refresh feature.
/// Replaces the in-memory MaintenanceScheduler with state that survives restarts.
///
/// How it works:
/// 1. On startup, ensures ScheduledTask documents exist for each task type
/// 2. Sets @refresh metadata to NextRunAt — RavenDB removes @refresh at the scheduled time
/// 3. A polling loop detects tasks whose NextRunAt has passed and executes them
/// 4. After execution, sets the next @refresh and updates the document
///
/// The Refresh feature acts as a persistent alarm clock: it modifies the document at
/// the scheduled time (removing @refresh), which survives service restarts. On startup,
/// the service checks for any overdue tasks and runs them immediately.
/// </summary>
public sealed class ScheduledTaskService : IDisposable
{
    private readonly IDocumentStore _documentStore;
    private readonly IEidetStore _eidetStore;
    private readonly MemoryService _memorySvc;
    private readonly IMaintenanceRunner _maintenance;
    private readonly ConsolidationEngine _consolidation;
    private readonly MaintenanceConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    /// <summary>Polling interval to check for due tasks. Short since Refresh does the heavy lifting.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>Initial delay before first poll to let the service fully start.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    public ScheduledTaskService(
        IDocumentStore documentStore,
        IEidetStore eidetStore,
        MemoryService memorySvc,
        IMaintenanceRunner maintenance,
        ConsolidationEngine consolidation,
        MaintenanceConfig config)
    {
        _documentStore = documentStore;
        _eidetStore = eidetStore;
        _memorySvc = memorySvc;
        _maintenance = maintenance;
        _consolidation = consolidation;
        _config = config;
    }

    /// <summary>
    /// Ensures task documents exist and starts the background polling loop.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await EnsureTaskDocumentsAsync(ct);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollingTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Returns the current state of all scheduled tasks.
    /// </summary>
    public async Task<List<ScheduledTask>> GetTasksAsync(CancellationToken ct = default)
    {
        using var session = _documentStore.OpenAsyncSession();
        var tasks = new List<ScheduledTask>();

        foreach (var type in Enum.GetValues<ScheduledTaskType>())
        {
            var task = await session.LoadAsync<ScheduledTask>(ScheduledTask.MakeId(type), ct);
            if (task is not null)
                tasks.Add(task);
        }

        return tasks;
    }

    /// <summary>
    /// Ensures a ScheduledTask document exists for each task type.
    /// If the document already exists, updates the interval from config but preserves run history.
    /// Sets @refresh on any task that's due or doesn't have one set.
    /// </summary>
    private async Task EnsureTaskDocumentsAsync(CancellationToken ct)
    {
        using var session = _documentStore.OpenAsyncSession();
        var now = DateTime.UtcNow;

        await EnsureTaskAsync(session, ScheduledTaskType.Maintenance, _config.IntervalHours, now, ct);
        await EnsureTaskAsync(session, ScheduledTaskType.Consolidation, _config.ConsolidationIntervalHours, now, ct);

        await session.SaveChangesAsync(ct);
    }

    private static async Task EnsureTaskAsync(
        IAsyncDocumentSession session, ScheduledTaskType type, int intervalHours, DateTime now, CancellationToken ct)
    {
        var id = ScheduledTask.MakeId(type);
        var task = await session.LoadAsync<ScheduledTask>(id, ct);

        if (task is null)
        {
            // First time — create the document, schedule first run after a short delay
            task = new ScheduledTask
            {
                Id = id,
                TaskType = type,
                IntervalHours = intervalHours,
                NextRunAt = now + TimeSpan.FromMinutes(type == ScheduledTaskType.Maintenance ? 5 : 2),
                Status = ScheduledTaskStatus.Pending,
                CreatedAt = now,
            };
            await session.StoreAsync(task, id, ct);
        }
        else
        {
            // Update interval from config (in case it changed)
            task.IntervalHours = intervalHours;

            // If it was running when the service died, reset to pending
            if (task.Status == ScheduledTaskStatus.Running)
            {
                task.Status = ScheduledTaskStatus.Pending;
                task.LastError = "Service restarted while task was running";
            }
        }

        // Set @refresh so RavenDB modifies the document at NextRunAt
        SetRefresh(session, task);
    }

    private static void SetRefresh(IAsyncDocumentSession session, ScheduledTask task)
    {
        var meta = session.Advanced.GetMetadataFor(task);
        meta["@refresh"] = task.NextRunAt.ToString("o");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Initial delay to let the service stabilize
        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAndRunDueTasksAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EidetLog.Error("ScheduledTaskService poll error", ex);
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAndRunDueTasksAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var type in Enum.GetValues<ScheduledTaskType>())
        {
            if (ct.IsCancellationRequested) break;

            using var session = _documentStore.OpenAsyncSession();
            var id = ScheduledTask.MakeId(type);
            var task = await session.LoadAsync<ScheduledTask>(id, ct);

            if (task is null) continue;
            if (task.Status == ScheduledTaskStatus.Running) continue; // Already running
            if (task.NextRunAt > now) continue; // Not due yet

            // Task is due — execute it
            await ExecuteTaskAsync(session, task, ct);
        }
    }

    private async Task ExecuteTaskAsync(IAsyncDocumentSession session, ScheduledTask task, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTime.UtcNow;

        // Mark as running
        task.Status = ScheduledTaskStatus.Running;
        task.LastRunAt = now;
        task.LastError = null;
        await session.SaveChangesAsync(ct);

        try
        {
            // Get all repos and run the task for each
            var repoIds = await _eidetStore.GetDistinctRepoIdsAsync(ct);

            foreach (var repoId in repoIds)
            {
                if (ct.IsCancellationRequested) break;

                switch (task.TaskType)
                {
                    case ScheduledTaskType.Maintenance:
                        var isActive = _memorySvc.IsRepoActive(repoId);
                        await _maintenance.RunAsync(new MaintenanceRequest { RepoId = repoId, IsRepoActive = isActive });
                        break;

                    case ScheduledTaskType.Consolidation:
                        await _consolidation.ConsolidateAsync(repoId);
                        break;
                }
            }

            sw.Stop();
            task.Status = ScheduledTaskStatus.Completed;
            task.LastCompletedAt = DateTime.UtcNow;
            task.LastDurationMs = sw.ElapsedMilliseconds;
            task.RunCount++;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            task.Status = ScheduledTaskStatus.Pending;
            task.LastError = "Cancelled due to shutdown";
        }
        catch (Exception ex)
        {
            sw.Stop();
            task.Status = ScheduledTaskStatus.Failed;
            task.LastDurationMs = sw.ElapsedMilliseconds;
            task.LastError = ex.Message;
            task.ErrorCount++;
            EidetLog.Error($"Scheduled task {task.TaskType} failed", ex);
        }

        // Schedule next run
        task.NextRunAt = DateTime.UtcNow + TimeSpan.FromHours(task.IntervalHours);
        SetRefresh(session, task);
        await session.SaveChangesAsync(ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _pollingTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* shutting down */ }
        _cts?.Dispose();
    }
}
