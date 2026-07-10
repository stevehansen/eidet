using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Runs the Reflector for the request's repo — mints net-new memories from positive feedback residue.
/// Off-MCP (maintenance-time synthesis, not a session tool); reachable via REST/CLI. <c>dry_run=true</c>
/// returns candidates without writing; <c>source</c> narrows which residue arm is mined.
/// </summary>
public sealed class ReflectToolHandler : IToolHandler
{
    private readonly ReflectionEngine _reflection;

    public ReflectToolHandler(ReflectionEngine reflection) => _reflection = reflection;

    public string Name => "eidet_reflect";
    public string UsageOp => "Reflect";
    public bool McpExposed => false;

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_reflect",
        Description = "Synthesize net-new memories from positive feedback residue (echoes, done loose ends, contradicted verdicts). Use dry_run to preview.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var dryRun = ToolArgs.GetBool(request.Arguments, "dry_run");
        var source = ToolArgs.GetEnum<ReflectionSource>(request.Arguments, "source") ?? ReflectionSource.All;
        var normalizedRepoId = RepoIdNormalizer.Normalize(request.RepoId);

        var result = await _reflection.ReflectAsync(normalizedRepoId, dryRun, source, request.Ct);

        var payload = new
        {
            candidates = result.Candidates.Count,
            memoriesCreated = result.MemoriesCreated,
            dryRun,
            source = source.ToString(),
            proposals = result.Candidates.Select(c => new
            {
                content = c.Content,
                type = c.Type.ToString().ToLowerInvariant(),
                valence = c.Valence.ToString().ToLowerInvariant(),
                tags = c.Tags,
                importance = c.Importance,
                provenance = c.Provenance.ToString(),
                derivedFrom = c.DerivedFrom,
            }),
        };

        if (result.Candidates.Count == 0)
            return ToolResult.Ok(payload, "No reflection candidates. Need net-echoed memories, done loose ends, or contradicted verdicts as residue.", count: 0);

        var lines = new List<string>
        {
            dryRun
                ? $"Reflection preview: {result.Candidates.Count} candidate(s)"
                : $"Reflected: {result.MemoriesCreated} memory(ies) minted from {result.Candidates.Count} candidate(s)",
        };
        foreach (var c in result.Candidates)
            lines.Add($"  [{c.Type.ToString().ToLowerInvariant()}] {Truncate(c.Content, 100)}");

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
            ["source"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("all", "echoes", "looseends", "drift"),
                ["description"] = "Which residue arm to mine (default: all).",
            },
        },
        ["required"] = new JsonArray(),
    };
}
