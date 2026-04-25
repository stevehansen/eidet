using System.Net;
using System.Text.Json;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoints for maintenance, quality reports, and the persisted scheduler view.
/// Maintenance routes through <see cref="ToolDispatcher"/> for parity with MCP; quality
/// and scheduler call their services directly and gate on their availability.
/// </summary>
internal sealed class MaintenanceEndpoints
{
    private readonly ToolDispatcher _dispatcher;
    private readonly QualityService? _quality;
    private readonly ScheduledTaskService? _scheduledTasks;
    private readonly UsageTracker? _usage;

    public MaintenanceEndpoints(ToolDispatcher dispatcher, QualityService? quality,
        ScheduledTaskService? scheduledTasks, UsageTracker? usage)
    {
        _dispatcher = dispatcher;
        _quality = quality;
        _scheduledTasks = scheduledTasks;
        _usage = usage;
    }

    public async Task Maintenance(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var args = JsonDocument.Parse("{}").RootElement;
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_maintenance", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task Quality(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_quality is null) { await HttpJson.WriteAsync(ctx, new { error = "Quality service not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        using var scope = _usage?.StartScope(repo, "Quality");
        var report = await _quality.AnalyzeAsync(repo, ct);
        await HttpJson.WriteAsync(ctx, report);
    }

    public async Task ScheduledTasks(HttpListenerContext ctx, CancellationToken ct)
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
