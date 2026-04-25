using System.Text.Json.Nodes;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Soft-deletes a memory by setting its validity end. Returns 404 when the id is unknown.
/// </summary>
public sealed class ForgetToolHandler : IToolHandler
{
    private readonly MemoryService _svc;

    public ForgetToolHandler(MemoryService svc) => _svc = svc;

    public string Name => "eidet_forget";
    public string UsageOp => "Forget";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_forget",
        Description = "Soft-delete a memory by setting its validity end date. Creates an audit trail observation.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var id = ToolArgs.RequireString(request.Arguments, "id");
        var reason = ToolArgs.GetString(request.Arguments, "reason");

        var ok = await _svc.ForgetAsync(id, reason, ct: request.Ct);
        if (!ok)
            return ToolResult.NotFound($"Memory not found: {id}");

        return ToolResult.Ok(
            payload: new { forgotten = true, id },
            summary: $"Memory {id} has been invalidated.",
            count: 1);
    }

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Memory ID to forget.",
            },
            ["reason"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Why this memory is being forgotten.",
            },
        },
        ["required"] = new JsonArray { "id" },
    };
}
