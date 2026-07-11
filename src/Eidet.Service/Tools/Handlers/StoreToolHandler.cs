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
        Description = "Store a memory (observation, insight, procedure, or heuristic). Content is validated through secret scanning and signal gates before storage. To record a failure/dead-end (\"tried X, does not work\"), pass negative:true — it stores a refuting, long-lived memory (type defaults to heuristic) so a future session recalls the dead-end before repeating it. On a context-eviction or compaction warning, store any durable finding not yet saved — Eidet memory survives compaction; in-context notes do not.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;

        var content = ToolArgs.RequireString(args, "content");

        // Explicit valence wins over the negative shorthand; negative:true ⇒ Refuting.
        var negative = ToolArgs.GetBool(args, "negative");
        var valence = ToolArgs.GetEnum<Valence>(args, "valence")
                      ?? (negative ? Valence.Refuting : Valence.Neutral);

        var typeStr = ToolArgs.GetString(args, "type");
        MemoryType type;
        if (typeStr is not null)
        {
            if (!Enum.TryParse(typeStr, true, out type))
                return ToolResult.BadRequest($"Invalid type: {typeStr}. Use: observation, insight, procedure, heuristic.");
        }
        else if (valence != Valence.Neutral)
        {
            // A dead-end/cautionary memory with no explicit type belongs on the near-immortal,
            // L1-visible Heuristic lifecycle so it resurfaces before the failure is repeated.
            type = MemoryType.Heuristic;
        }
        else
        {
            return ToolResult.BadRequest("Invalid type: type is required (or pass negative:true / valence). Use: observation, insight, procedure, heuristic.");
        }

        // Dead-end conveniences follow the resolved Refuting stance, not just the `negative`
        // shorthand — so an explicit valence:"refuting" is as discoverable and long-lived as
        // negative:true (same tag, same importance default).
        var isDeadEnd = valence == Valence.Refuting;
        var tags = ToolArgs.GetStringArray(args, "tags");
        if (isDeadEnd && !tags.Contains("dead-end", StringComparer.OrdinalIgnoreCase))
            tags.Add("dead-end");
        var importance = ToolArgs.GetFloat(args, "importance", isDeadEnd ? 0.7f : 0.5f);
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
            Valence = valence,
            Stage = ToolArgs.GetEnum<FunctionalStage>(args, "stage") ?? FunctionalStage.None,
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
                ["description"] = "Memory type: observation, insight, procedure, or heuristic. Optional when negative:true or a non-neutral valence is set (defaults to heuristic).",
            },
            ["negative"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Shorthand for a dead-end: sets valence=refuting, defaults type to heuristic, importance to 0.7, and adds the 'dead-end' tag. Use for 'tried X, does not work'.",
            },
            ["valence"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "neutral", "affirming", "refuting", "cautionary" },
                ["description"] = "Explicit stance toward the subject (overrides negative): refuting (dead-end), cautionary (works but has sharp edges), affirming (holds), or neutral (default).",
            },
            ["stage"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "analyze", "locate", "edit", "test", "debug", "deploy" },
                ["description"] = "Functional subtask this memory applies to. Recall can hard-filter by stage; a memory with no stage applies to every stage. Omit for stage-agnostic knowledge.",
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
            ["required"] = new JsonArray { "content" },
        };
    }
}
