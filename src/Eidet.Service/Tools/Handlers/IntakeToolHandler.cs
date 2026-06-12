using System.Text.Json.Nodes;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Ingests project files as seed memories. Two modes:
/// - whole-repo (default): infers package metadata, imports relevant docs and READMEs
/// - path-scoped (when <c>path</c> is set): walks a directory with <c>pattern</c>/<c>recursive</c>
/// </summary>
public sealed class IntakeToolHandler : IToolHandler
{
    private readonly IntakeService _intake;

    public IntakeToolHandler(IntakeService intake) => _intake = intake;

    public string Name => "eidet_intake";
    public string UsageOp => "Intake";
    public bool McpExposed => false;

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_intake",
        Description = "Ingest project files as seed memories. Defaults to whole-repo intake; use 'path' to scope to a directory.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;
        var dryRun = ToolArgs.GetBool(args, "dry_run");
        var path = ToolArgs.GetString(args, "path");

        IntakeResult result;
        if (!string.IsNullOrEmpty(path))
        {
            var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(request.RepoId, path);
            if (!Directory.Exists(resolvedPath))
                return ToolResult.NotFound($"Directory not found: {resolvedPath}");

            var pattern = ToolArgs.GetString(args, "pattern") ?? "*.md";
            var recursive = ToolArgs.GetBool(args, "recursive", true);
            var importance = ToolArgs.GetFloat(args, "importance", 0.6f);
            var extraTags = ToolArgs.GetStringArray(args, "tags");

            result = await _intake.IngestDocsAsync(resolvedPath, resolvedPath, recursive, pattern, importance,
                extraTags.Count > 0 ? extraTags : null, dryRun, request.Ct);
        }
        else
        {
            var projectPath = Path.IsPathRooted(request.RepoId) ? request.RepoId : Directory.GetCurrentDirectory();
            result = await _intake.IngestAsync(request.RepoId, projectPath, dryRun, request.Ct);
        }

        var mode = dryRun ? "Would ingest" : "Ingested";
        var lines = new List<string> { $"{mode}: {result.NewCount} new, {result.SkippedCount} skipped" };

        if (result.ProducedPackages.Count > 0)
            lines.Add($"Produces: {string.Join(", ", result.ProducedPackages)}");
        if (result.DetectedLinks.Count > 0)
            lines.Add($"Dependencies: {string.Join(", ", result.DetectedLinks.Select(l => l.TargetRepoId))}");

        foreach (var item in result.Items.Take(20))
        {
            var status = item.WasSkipped ? $"[SKIP: {item.SkipReason}]" : "[NEW]";
            lines.Add($"  {status} {item.Source} -> {item.Type}: {Truncate(item.Content, 80)}");
        }
        if (result.Items.Count > 20)
            lines.Add($"  ... and {result.Items.Count - 20} more");

        return ToolResult.Ok(
            payload: new
            {
                newCount = result.NewCount,
                skippedCount = result.SkippedCount,
                dependencies = result.DetectedLinks.Count,
                produces = result.ProducedPackages,
            },
            summary: string.Join("\n", lines),
            count: result.NewCount);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static JsonObject BuildSchema()
    {
        var props = new JsonObject
        {
            ["dry_run"] = new JsonObject { ["type"] = "boolean" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Optional directory to scope intake to." },
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Glob pattern (default *.md)." },
            ["recursive"] = new JsonObject { ["type"] = "boolean" },
            ["importance"] = new JsonObject { ["type"] = "number" },
            ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(),
        };
    }
}
