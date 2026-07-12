using System.Text.Json;
using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Updates an existing memory. Content edits create a versioned supersession; metadata edits update
/// in place. Optional enrichment fields (one-liner, summary, foresight hint) are wired through for
/// Web UI / REST consumers.
/// </summary>
public sealed class EditToolHandler : IToolHandler
{
    private readonly MemoryService _svc;

    public EditToolHandler(MemoryService svc) => _svc = svc;

    public string Name => "eidet_edit";
    public string UsageOp => "Store";
    public bool McpExposed => false;

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_edit",
        Description = "Update an existing memory. Content changes create versioned supersession; metadata edits update in place.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;
        var id = ToolArgs.RequireString(args, "id");
        var content = ToolArgs.GetString(args, "content");
        var tags = ToolArgs.GetStringArray(args, "tags");
        var importance = ToolArgs.GetFloatOrNull(args, "importance");
        var confidence = ToolArgs.GetFloatOrNull(args, "confidence");
        var oneLiner = ToolArgs.GetString(args, "oneLiner");
        var summary = ToolArgs.GetString(args, "summary");
        var foresightHint = ToolArgs.GetString(args, "foresightHint");

        var typeStr = ToolArgs.GetString(args, "type");
        MemoryType? type = typeStr != null && Enum.TryParse<MemoryType>(typeStr, true, out var t) ? t : null;
        var stage = ToolArgs.GetEnum<FunctionalStage>(args, "stage");
        var expectedSha = ToolArgs.GetString(args, "expectedContentSha256");

        var outcome = await _svc.EditAsync(id, new EditOptions
        {
            Content = content,
            Tags = tags.Count > 0 ? tags : null,
            Importance = importance,
            Confidence = confidence,
            Type = type,
            Stage = stage,
            OneLiner = oneLiner,
            Summary = summary,
            ForesightHint = foresightHint,
            ExpectedContentSha256 = expectedSha,
        }, request.Ct);

        return outcome switch
        {
            // Stale precondition → 409 with the current hash so the caller can re-read/merge.
            EditOutcome.PreconditionFailed => ToolResult.Conflict(
                $"content_sha256 precondition failed for {id}; re-read and retry.",
                duplicateId: await CurrentShaAsync(id, request.Ct)),
            EditOutcome.NotFound => ToolResult.NotFound($"Memory not found or update rejected: {id}"),
            _ => ToolResult.Ok(
                payload: new { updated = true, id, superseded = outcome == EditOutcome.Superseded },
                summary: $"Memory {id} updated successfully.",
                count: 1),
        };
    }

    private async Task<string?> CurrentShaAsync(string id, CancellationToken ct)
    {
        var chain = await _svc.GetVersionChainAsync(id, ct);
        return chain.Count > 0 ? Eidet.Core.Domain.ContentHash.Of(chain[0].Content) : null;
    }

    private static JsonObject BuildSchema()
    {
        var props = new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Memory ID to edit." },
            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "New content (creates a new version)." },
            ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["importance"] = new JsonObject { ["type"] = "number", ["description"] = "0.0-1.0." },
            ["confidence"] = new JsonObject { ["type"] = "number", ["description"] = "0.0-1.0." },
            ["type"] = new JsonObject { ["type"] = "string", ["description"] = "Reclassify: observation, insight, procedure, heuristic." },
            ["stage"] = new JsonObject { ["type"] = "string", ["description"] = "Re-tag functional stage: analyze, locate, edit, test, debug, deploy." },
            ["expectedContentSha256"] = new JsonObject { ["type"] = "string", ["description"] = "Optimistic-concurrency precondition: SHA256 of the content you read. Mismatch → 409, no edit." },
            ["oneLiner"] = new JsonObject { ["type"] = "string" },
            ["summary"] = new JsonObject { ["type"] = "string" },
            ["foresightHint"] = new JsonObject { ["type"] = "string" },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray { "id" },
        };
    }
}
