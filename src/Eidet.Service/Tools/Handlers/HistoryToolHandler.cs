using System.Text.Json.Nodes;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Returns the supersession chain for a memory id (newest first).
/// </summary>
public sealed class HistoryToolHandler : IToolHandler
{
    private readonly MemoryService _svc;

    public HistoryToolHandler(MemoryService svc) => _svc = svc;

    public string Name => "eidet_history";
    public string UsageOp => "History";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_history",
        Description = "Get the version chain for a memory, showing how it evolved over time via supersession.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var id = ToolArgs.RequireString(request.Arguments, "id");
        var chain = await _svc.GetVersionChainAsync(id, request.Ct);

        if (chain.Count == 0)
            return ToolResult.Ok(
                payload: new { id, chain = Array.Empty<object>() },
                summary: $"Memory not found: {id}",
                count: 0);

        var lines = new List<string> { $"Version history for {id} ({chain.Count} version(s)):" };
        for (var i = 0; i < chain.Count; i++)
        {
            var e = chain[i];
            var current = i == 0 ? " (current)" : "";
            var expired = e.Validity.ValidUntil != null ? $" [expired: {e.ForgetReason ?? "superseded"}]" : "";
            lines.Add($"  {i + 1}. {e.Id}{current}{expired}");
            lines.Add($"     Created: {e.CreatedAt:u} | Importance: {e.Importance:F2}");
            lines.Add($"     {Truncate(e.Content, 100)}");
        }

        return ToolResult.Ok(
            payload: new { id, chain },
            summary: string.Join("\n", lines),
            count: chain.Count);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Memory ID to get history for.",
            },
        },
        ["required"] = new JsonArray { "id" },
    };
}
