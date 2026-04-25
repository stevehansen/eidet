using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Hybrid recall through <see cref="MemoryService.RecallAsync"/>. Returns the scored result list
/// as the structured payload and a one-line-per-hit summary for MCP.
/// </summary>
public sealed class RecallToolHandler : IToolHandler
{
    private readonly MemoryService _svc;

    public RecallToolHandler(MemoryService svc) => _svc = svc;

    public string Name => "eidet_recall";
    public string UsageOp => "Recall";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_recall",
        Description = "Search memories using hybrid retrieval (vector similarity + full-text + metadata filters). Returns scored results with staleness warnings.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;

        var query = new MemoryQuery
        {
            Text = ToolArgs.RequireString(args, "query"),
            Type = ToolArgs.GetEnum<MemoryType>(args, "type"),
            Tags = ToolArgs.GetStringArray(args, "tags"),
            Limit = ToolArgs.GetInt(args, "limit", 10),
            IncludeExpired = ToolArgs.GetBool(args, "include_expired"),
            CrossRepo = ToolArgs.GetBool(args, "cross_repo", defaultValue: true),
        };

        var results = await _svc.RecallAsync(request.RepoId, query, request.Ct);

        if (results.Count == 0)
            return ToolResult.Ok(
                payload: new { repo = request.RepoId, query = query.Text, results = Array.Empty<MemorySearchResult>() },
                summary: "No memories found.",
                count: 0);

        var lines = new List<string> { $"{results.Count} memory(ies) found:" };
        foreach (var r in results)
        {
            var prefix = r.Type switch
            {
                MemoryType.Insight => "[I]",
                MemoryType.Observation => "[O]",
                MemoryType.Procedure => "[P]",
                MemoryType.Heuristic => "[H]",
                _ => "[?]",
            };
            var stale = r.StalenessWarning != null ? $" {r.StalenessWarning}" : "";
            var display = r.OneLiner ?? r.Summary ?? Truncate(r.Content, 120);
            lines.Add($"  {prefix} {display}{stale}");
            lines.Add($"      id={r.Id} importance={r.Importance:F2} score={r.Score:F2}");
        }

        return ToolResult.Ok(
            payload: new { repo = request.RepoId, query = query.Text, results },
            summary: string.Join("\n", lines),
            count: results.Count);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static JsonObject BuildSchema()
    {
        var props = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Natural language search query.",
            },
            ["type"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Filter by type: observation, insight, procedure, heuristic.",
            },
            ["tags"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Filter by tags (AND logic).",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Max results 1-50 (default 10).",
            },
            ["include_expired"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Include forgotten/expired memories (default false).",
            },
            ["cross_repo"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Search linked repos and layers (default true).",
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray { "query" },
        };
    }
}
