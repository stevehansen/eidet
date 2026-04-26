using System.Net;
using Eidet.Core.Services;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoint for the persisted scheduler view. Returns 503 when the
/// scheduler service is not configured.
/// </summary>
internal sealed class ScheduledTasksEndpoint
{
    private readonly ScheduledTaskService? _scheduledTasks;

    public ScheduledTasksEndpoint(ScheduledTaskService? scheduledTasks)
    {
        _scheduledTasks = scheduledTasks;
    }

    public async Task List(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_scheduledTasks is null)
        {
            await HttpJson.WriteAsync(ctx, new { error = "Scheduler not available" }, 503);
            return;
        }

        var tasks = await _scheduledTasks.GetTasksAsync(ct);
        await HttpJson.WriteAsync(ctx, new { tasks });
    }
}
