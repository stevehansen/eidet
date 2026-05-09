using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Stores a memory through <see cref="MemoryService.StoreAsync"/>. Honors WriteValidator gates
/// (rejection → 422, duplicate → 409) and returns a structured payload <c>{ id }</c> for REST and
/// a one-liner summary for MCP.
/// </summary>
public sealed class StoreToolHandler : IToolHandler
{
    private readonly MemoryService _svc;

    public StoreToolHandler(MemoryService svc) => _svc = svc;

    public string Name => "eidet_store";
    public string UsageOp => "Store";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_store",
        Description = "Store a memory (observation, insight, procedure, or heuristic). Content is validated through secret scanning and signal gates before storage.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;

        var content = ToolArgs.RequireString(args, "content");
        var typeStr = ToolArgs.RequireString(args, "type");
        if (!Enum.TryParse<MemoryType>(typeStr, true, out var type))
            return ToolResult.BadRequest($"Invalid type: {typeStr}. Use: observation, insight, procedure, heuristic.");

        var tags = ToolArgs.GetStringArray(args, "tags");
        var importance = ToolArgs.GetFloat(args, "importance", 0.5f);
        var supersedes = ToolArgs.GetString(args, "supersedes");
        var source = ToolArgs.GetString(args, "source") ?? "claude-session";
        var sessionId = ToolArgs.GetString(args, "sessionId");

        var provenanceStr = ToolArgs.GetString(args, "provenance");
        MemoryProvenance? provenance = provenanceStr switch
        {
            null => null,
            var s when s.Equals("Bundle", StringComparison.OrdinalIgnoreCase) => MemoryProvenance.Pack,
            var s when Enum.TryParse<MemoryProvenance>(s, true, out var p) => p,
            _ => null,
        };

        var result = await _svc.StoreAsync(new StoreOptions(request.RepoId, content, type)
        {
            Tags = tags.Count > 0 ? tags : null,
            Importance = importance,
            Source = source,
            SessionId = sessionId,
            Supersedes = supersedes,
            Provenance = provenance,
        }, request.Ct);

        if (result.DuplicateId != null)
            return ToolResult.Conflict(
                $"Near-duplicate of existing memory: {result.DuplicateId}",
                duplicateId: result.DuplicateId);

        if (!result.Success)
            return ToolResult.Rejected(result.Reason!);

        return ToolResult.Ok(
            payload: new { id = result.Id },
            summary: $"Stored: {result.Id}",
            count: 1);
    }

    private static JsonObject BuildSchema()
    {
        var props = new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The memory content to store. Must be 20+ chars, specific, and self-contained.",
            },
            ["type"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Memory type: observation, insight, procedure, or heuristic.",
            },
            ["tags"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Tags for filtering and discovery.",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
            ["importance"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Importance score 0.0-1.0 (default 0.5).",
            },
            ["supersedes"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "ID of memory this replaces (creates version chain).",
            },
            ["provenance"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Origin: user_stated, agent_inferred, tool_output.",
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray { "content", "type" },
        };
    }
}
