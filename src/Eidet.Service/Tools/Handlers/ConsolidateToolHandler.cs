using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Runs the consolidation engine for the request's repo. <c>dry_run=true</c> returns candidates
/// without writing.
/// </summary>
public sealed class ConsolidateToolHandler : IToolHandler
{
    private readonly ConsolidationEngine _consolidation;

    public ConsolidateToolHandler(ConsolidationEngine consolidation) => _consolidation = consolidation;

    public string Name => "eidet_consolidate";
    public string UsageOp => "Consolidate";
    public bool McpExposed => false;

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_consolidate",
        Description = "Merge related observations into insights when 3+ are detected. Use dry_run to preview.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var dryRun = ToolArgs.GetBool(request.Arguments, "dry_run");
        var normalizedRepoId = RepoIdNormalizer.Normalize(request.RepoId);
        var result = await _consolidation.ConsolidateAsync(normalizedRepoId, dryRun, request.Ct);

        var payload = new
        {
            candidates = result.Candidates.Count,
            insightsCreated = result.InsightsCreated,
            insightsBoosted = result.InsightsBoosted,
            dryRun,
            groups = result.Candidates.Select(c => new
            {
                observations = c.ObservationIds,
                proposedContent = c.ProposedContent,
                tags = c.Tags,
                importance = c.ProposedImportance,
            }),
        };

        if (result.Candidates.Count == 0)
            return ToolResult.Ok(payload, "No consolidation candidates found. Need 3+ related observations.", count: 0);

        var lines = new List<string>();
        if (dryRun)
            lines.Add($"Consolidation preview: {result.Candidates.Count} candidate(s)");
        else
        {
            var parts = new List<string>();
            if (result.InsightsCreated > 0) parts.Add($"{result.InsightsCreated} created");
            if (result.InsightsBoosted > 0) parts.Add($"{result.InsightsBoosted} boosted");
            lines.Add($"Consolidated: {string.Join(", ", parts)} from {result.Candidates.Count} group(s)");
        }

        foreach (var c in result.Candidates)
        {
            lines.Add($"\n  Group ({c.ObservationIds.Count} observations):");
            lines.Add($"  -> {Truncate(c.ProposedContent, 100)}");
            lines.Add($"  Tags: {string.Join(", ", c.Tags)} | Importance: {c.ProposedImportance:F2}");
        }

        return ToolResult.Ok(payload, string.Join("\n", lines), count: result.Candidates.Count);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["dry_run"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "If true, return candidates without writing.",
            },
        },
        ["required"] = new JsonArray(),
    };
}
