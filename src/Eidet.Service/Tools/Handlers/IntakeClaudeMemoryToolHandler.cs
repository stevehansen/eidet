using System.Text.Json.Nodes;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Imports Claude Code's native per-project memory (<c>~/.claude/projects/&lt;slug&gt;/memory</c>)
/// as seed memories (issue #66). A distinct opt-in verb because the source lies outside the
/// repo. Off-MCP like the other intake handlers — reachable via REST/CLI.
/// </summary>
public sealed class IntakeClaudeMemoryToolHandler : IToolHandler
{
    private readonly IntakeService _intake;

    public IntakeClaudeMemoryToolHandler(IntakeService intake) => _intake = intake;

    public string Name => "eidet_intake_claude_memory";
    public string UsageOp => "IntakeClaudeMemory";
    public bool McpExposed => false;

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_intake_claude_memory",
        Description = "Import Claude Code's native per-project memory directory as seed memories.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var dryRun = ToolArgs.GetBool(request.Arguments, "dry_run");
        var projectPath = Path.IsPathRooted(request.RepoId) ? request.RepoId : Directory.GetCurrentDirectory();
        var result = await _intake.IngestClaudeMemoryAsync(request.RepoId, projectPath, dryRun, request.Ct);

        var mode = dryRun ? "Would import" : "Imported";
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

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["dry_run"] = new JsonObject { ["type"] = "boolean" },
        },
        ["required"] = new JsonArray(),
    };
}
