using System.Text.Json.Nodes;
using Eidet.Core.Services;
using Eidet.Core.Update;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Returns the dense-packed L0+L1 context block for session start.
/// Auto-intake (MCP-only, side-effecting) lives in <see cref="McpServer"/>; this handler is
/// pure read.
/// </summary>
public sealed class ContextToolHandler : IToolHandler
{
    private readonly MemoryService _svc;

    public ContextToolHandler(MemoryService svc) => _svc = svc;

    public string Name => "eidet_context";
    public string UsageOp => "Context";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_context",
        Description = "Get compact L0 (identity) + L1 (top-K scored memories) context block for session start. Under 600 tokens. Call this after a restart or compaction to reload what you stored — Eidet memory survives context clears.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var maxTokens = ToolArgs.GetInt(request.Arguments, "max_tokens", 600);
        var context = await _svc.GetContextAsync(request.RepoId, maxTokens, request.Ct);

        // Appended outside the token budget: it is one line, at most once per process, and an
        // agent relaying it is the only way the news reaches a user who never runs the CLI.
        var notice = UpdateNotice.TryTake();
        if (notice is not null)
            context += $"\n\n[{notice}]";

        return ToolResult.Ok(
            payload: new { repo = request.RepoId, context },
            summary: context,
            count: 1);
    }

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["max_tokens"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Token budget (default 600).",
            },
        },
        ["required"] = new JsonArray(),
    };
}
