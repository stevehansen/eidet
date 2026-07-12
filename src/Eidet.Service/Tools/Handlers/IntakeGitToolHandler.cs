using System.Text.Json.Nodes;
using Eidet.Core.Intake.Git;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Seeds Procedure/Insight memories from git commit history (issue #40). Defaults resume
/// from the per-repo watermark; <c>since</c>/<c>max_commits</c>/<c>all_commits</c> are the
/// advanced knobs. Off-MCP like <see cref="IntakeToolHandler"/> — reachable via REST/CLI.
/// </summary>
public sealed class IntakeGitToolHandler : IToolHandler
{
    private readonly IntakeService _intake;

    public IntakeGitToolHandler(IntakeService intake) => _intake = intake;

    public string Name => "eidet_intake_git";
    public string UsageOp => "IntakeGit";
    public bool McpExposed => false;

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_intake_git",
        Description = "Seed Procedure/Insight memories from git commit history. Defaults resume from the last-run watermark.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;
        var dryRun = ToolArgs.GetBool(args, "dry_run");
        var options = new GitIntakeOptions(
            Since: ToolArgs.GetString(args, "since"),
            MaxCommits: ToolArgs.GetInt(args, "max_commits", 500),
            IncludeNonConventional: ToolArgs.GetBool(args, "all_commits"));

        var projectPath = Path.IsPathRooted(request.RepoId) ? request.RepoId : Directory.GetCurrentDirectory();
        var result = await _intake.IngestGitAsync(request.RepoId, projectPath, options, dryRun, request.Ct);

        var mode = dryRun ? "Would mine" : "Mined";
        var lines = new List<string> { $"{mode}: {result.NewCount} new, {result.SkippedCount} skipped" };
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
            ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Exclusive lower-bound commit SHA (default: per-repo watermark)." },
            ["max_commits"] = new JsonObject { ["type"] = "number", ["description"] = "Upper bound on commits examined (default 500)." },
            ["all_commits"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also mine non-Conventional-Commits messages." },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(),
        };
    }
}
