using System.Text.Json.Nodes;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Echo (used) / fizzle (irrelevant) feedback for a recalled memory; tunes future scoring.
/// </summary>
public sealed class FeedbackToolHandler : IToolHandler
{
    private readonly MemoryService _svc;

    public FeedbackToolHandler(MemoryService svc) => _svc = svc;

    public string Name => "eidet_feedback";
    public string UsageOp => "Feedback";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_feedback",
        Description = "Provide echo (used) or fizzle (not used) feedback on a recalled memory. Adjusts importance and confidence scores.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var id = ToolArgs.RequireString(request.Arguments, "id");
        var used = ToolArgs.RequireBool(request.Arguments, "used");

        var ok = await _svc.ApplyFeedbackAsync(id, used, request.Ct);
        if (!ok)
            return ToolResult.NotFound($"Memory not found: {id}");

        var label = used ? "Echo" : "Fizzle";
        return ToolResult.Ok(
            payload: new { applied = true, id, used },
            summary: $"{label} feedback applied to {id}.",
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
                ["description"] = "Memory ID to provide feedback on.",
            },
            ["used"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "true = echo (memory was useful), false = fizzle (memory was irrelevant).",
            },
        },
        ["required"] = new JsonArray { "id", "used" },
    };
}
