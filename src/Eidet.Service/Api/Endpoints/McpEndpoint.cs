using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// MCP-over-HTTP bridge for <c>POST /mcp</c>. Maintains a per-repo
/// <see cref="McpServer"/> pool keyed by the optional <c>?repo=</c> override
/// (used by container overlays that want to reuse one HTTP listener for many
/// projects). 501s when the listener was started without an MCP server.
/// </summary>
internal sealed class McpEndpoint
{
    private readonly McpServer? _mcpServer;
    private readonly ConcurrentDictionary<string, McpServer> _pool = new();
    private readonly MemoryService _svc;
    private readonly IntakeService _intake;
    private readonly ConsolidationEngine _consolidation;
    private readonly IMaintenanceRunner _maintenance;

    public McpEndpoint(McpServer? mcpServer, MemoryService svc, IntakeService intake,
        ConsolidationEngine consolidation, IMaintenanceRunner maintenance)
    {
        _mcpServer = mcpServer;
        _svc = svc;
        _intake = intake;
        _consolidation = consolidation;
        _maintenance = maintenance;
    }

    public async Task Handle(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_mcpServer is null)
        {
            await HttpJson.WriteAsync(ctx, new { error = "MCP server not available" }, 501);
            return;
        }

        var repoOverride = ctx.Request.QueryString["repo"];
        var server = string.IsNullOrEmpty(repoOverride)
            ? _mcpServer
            : _pool.GetOrAdd(repoOverride, id =>
                new McpServer(_svc, _intake, _consolidation, _maintenance, id));

        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);

        var response = await server.ProcessRequestAsync(body, ct);

        if (response is null)
        {
            // Notification — no response needed (204 No Content)
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.OutputStream, response, McpServer.SerializerOptions, ct);
        ctx.Response.Close();
    }
}
