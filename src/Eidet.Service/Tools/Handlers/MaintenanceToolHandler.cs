using System.Text.Json.Nodes;
using Eidet.Core.Maintenance;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Runs the maintenance pipeline (TTL expiry, dedup, decay, consolidation, enrichment) for the
/// request's repo.
/// </summary>
public sealed class MaintenanceToolHandler : IToolHandler
{
    private readonly IMaintenanceRunner _maintenance;

    public MaintenanceToolHandler(IMaintenanceRunner maintenance)
    {
        _maintenance = maintenance;
    }

    public string Name => "eidet_maintenance";
    public string UsageOp => "Maintenance";
    public bool McpExposed => false;

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_maintenance",
        Description = "Run the maintenance pipeline: TTL expiry, dedup, importance decay, consolidation, optional Ollama enrichment.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var report = await _maintenance.RunAsync(request.RepoId, request.Ct);

        return ToolResult.Ok(
            payload: report,
            summary: report.ToString(),
            count: 1);
    }

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["required"] = new JsonArray(),
    };
}
