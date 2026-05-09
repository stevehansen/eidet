using System.Text.Json.Nodes;
using Eidet.Core;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Creates a cross-repo or memory-to-memory link by storing a relation insight on the source repo.
/// </summary>
public sealed class LinkToolHandler : IToolHandler
{
    private readonly MemoryService _svc;

    public LinkToolHandler(MemoryService svc) => _svc = svc;

    public string Name => "eidet_link";
    public string UsageOp => "Store";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_link",
        Description = "Create a cross-repo link or memory-to-memory link. Stores a relation insight tagged for discovery.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;
        var targetRepo = ToolArgs.RequireString(args, "target_repo");
        var relation = ToolArgs.RequireString(args, "relation");
        var targetMemoryId = ToolArgs.GetString(args, "target_memory_id");
        var source = ToolArgs.GetString(args, "source") ?? "claude-session";

        var sourceRepoId = RepoIdNormalizer.Normalize(request.RepoId);
        var targetRepoId = RepoIdNormalizer.Normalize(targetRepo);

        var content = targetMemoryId != null
            ? $"Memory link: {relation} -> {targetMemoryId}"
            : $"Cross-repo link: {relation} -> {targetRepoId}";

        var tags = targetMemoryId != null
            ? new List<string> { "memory-link", relation }
            : new List<string> { "cross-repo-link", relation };

        var result = await _svc.StoreAsync(new StoreOptions(sourceRepoId, content, MemoryType.Insight)
        {
            Tags = tags,
            Importance = 0.7f,
            Source = source,
        }, request.Ct);

        if (!result.Success)
            return ToolResult.Rejected(result.Reason!);

        return ToolResult.Ok(
            payload: new
            {
                id = result.Id,
                from = sourceRepoId,
                to = targetRepoId,
                targetMemoryId,
                relation,
            },
            summary: $"Link created: {sourceRepoId} --[{relation}]--> {targetRepoId} (ID: {result.Id})",
            count: 1);
    }

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["target_repo"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Target repo path or ID.",
            },
            ["relation"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Relation label, e.g. 'depends_on', 'extends', 'replaces'.",
            },
            ["target_memory_id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional. If set, link points at a specific memory rather than a repo.",
            },
            ["source"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Origin label for the stored relation insight.",
            },
        },
        ["required"] = new JsonArray { "target_repo", "relation" },
    };
}
