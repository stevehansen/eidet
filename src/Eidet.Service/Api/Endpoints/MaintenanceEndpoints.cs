using System.Net;
using System.Text.Json;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoint for the maintenance pipeline. Routes through <see cref="ToolDispatcher"/>
/// for parity with MCP.
/// </summary>
internal sealed class MaintenanceEndpoints
{
    private readonly ToolDispatcher _dispatcher;

    public MaintenanceEndpoints(ToolDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
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
}
