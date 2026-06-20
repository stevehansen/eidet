using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.LooseEnds;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Resolves a Loose End with a typed kind through <see cref="LooseEndService.ResolveAsync"/>.
/// <c>kind</c> is required at the tool layer (a defaulted Done would silently mislabel abandoned
/// work). Promote with a <c>promote_to</c> external ref links instead of minting; otherwise it
/// mints a gated <see cref="MemoryEntry"/>. NotFound → 404, invalid kind → 400.
/// </summary>
public sealed class ResolveToolHandler : IToolHandler
{
    private readonly LooseEndService _svc;

    public ResolveToolHandler(LooseEndService svc) => _svc = svc;

    public string Name => "eidet_resolve";
    public string UsageOp => "Resolve";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_resolve",
        Description = "Resolve a parked Loose End with a kind: done (handled), dropped (not worth doing), promoted (graduate into a gated memory or link an issue), or superseded (folded into another). Done-vs-dropped-vs-promoted is the quality signal — pick deliberately.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;

        var id = ToolArgs.RequireString(args, "id");

        var kindStr = ToolArgs.RequireString(args, "kind");
        if (!Enum.TryParse<ResolutionKind>(kindStr, true, out var kind))
            return ToolResult.BadRequest($"Invalid kind: {kindStr}. Use: done, dropped, promoted, superseded.");

        var note = ToolArgs.GetString(args, "note");
        var promoteType = ToolArgs.GetEnum<MemoryType>(args, "promote_type") ?? MemoryType.Insight;
        var promoteTo = ToolArgs.GetString(args, "promote_to");

        var result = await _svc.ResolveAsync(id, kind, new ResolveOptions
        {
            Note = note,
            PromoteType = promoteType,
            ExternalRef = promoteTo,
        }, request.Ct);

        if (!result.Success)
        {
            return result.Reason == "not found"
                ? ToolResult.NotFound($"Loose End not found: {id}")
                : ToolResult.Rejected(result.Reason!);
        }

        return ToolResult.Ok(
            payload: new
            {
                id = result.Id,
                state = "resolved",
                kind = result.Kind?.ToString().ToLowerInvariant(),
                promotedToMemoryId = result.PromotedToMemoryId,
                externalRef = result.ExternalRef,
            },
            summary: $"Resolved {result.Id} as {result.Kind?.ToString().ToLowerInvariant()}.",
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
                ["description"] = "Loose End ID to resolve.",
            },
            ["kind"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Resolution kind: done, dropped, promoted, or superseded.",
            },
            ["note"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Why this was resolved (especially useful for dropped).",
            },
            ["promote_type"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "When kind=promoted and minting a memory: observation, insight (default), procedure, or heuristic.",
            },
            ["promote_to"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "When kind=promoted: an external issue ref (e.g. 'gh#412') to link instead of minting a memory.",
            },
        },
        ["required"] = new JsonArray { "id", "kind" },
    };
}
