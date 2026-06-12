using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Exports a memory pack. If <c>output</c> is set, writes a markdown/JSON pack file and returns
/// path + entry count. Otherwise returns the pack object as the structured payload (REST consumers).
/// </summary>
public sealed class PackExportToolHandler : IToolHandler
{
    private readonly ExportService? _export;

    public PackExportToolHandler(ExportService? export) => _export = export;

    public string Name => "eidet_pack_export";
    public string UsageOp => "PackExport";
    public bool McpExposed => false;

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_pack_export",
        Description = "Export a shareable memory pack (markdown or JSON) for the current repo, optionally filtered by type/tag/package.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        if (_export == null)
            return ToolResult.Internal("Pack export not available in this context.");

        var args = request.Arguments;
        var packId = ToolArgs.GetString(args, "pack_id") ?? ToolArgs.GetString(args, "bundle_id");
        if (string.IsNullOrEmpty(packId))
            throw new MissingToolArgumentException("pack_id");

        var name = ToolArgs.GetString(args, "name") ?? packId;
        var version = ToolArgs.GetString(args, "version") ?? "1.0.0";
        var author = ToolArgs.GetString(args, "author") ?? "";
        var description = ToolArgs.GetString(args, "description");
        var output = ToolArgs.GetString(args, "output");
        var packages = ToolArgs.GetStringArray(args, "packages");
        var tags = ToolArgs.GetStringArray(args, "tags");

        var typeStrs = ToolArgs.GetStringArray(args, "types");
        List<MemoryType>? types = typeStrs.Count > 0
            ? typeStrs.Where(t => Enum.TryParse<MemoryType>(t, true, out _))
                .Select(t => Enum.Parse<MemoryType>(t, true)).ToList()
            : null;

        var normalizedRepoId = RepoIdNormalizer.Normalize(request.RepoId);
        var pack = await _export.ExportPackAsync(normalizedRepoId, packId, name, version, author,
            types: types, tags: tags.Count > 0 ? tags : null,
            applicablePackages: packages.Count > 0 ? packages : null, ct: request.Ct);
        pack.Description = description;

        if (string.IsNullOrEmpty(output))
            return ToolResult.Ok(
                payload: pack,
                summary: $"Exported {pack.Entries.Count} memories in pack \"{pack.Name}\" v{pack.Version}",
                count: pack.Entries.Count);

        var outputPath = Path.IsPathRooted(output) ? output : Path.Combine(request.RepoId, output);
        await _export.ExportPackToFileAsync(pack, outputPath, request.Ct);

        var format = outputPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "markdown" : "JSON";
        return ToolResult.Ok(
            payload: new { entries = pack.Entries.Count, path = outputPath, format },
            summary: $"Exported {pack.Entries.Count} memories as {format} pack to {outputPath}",
            count: pack.Entries.Count);
    }

    private static JsonObject BuildSchema()
    {
        var props = new JsonObject
        {
            ["pack_id"] = new JsonObject { ["type"] = "string", ["description"] = "Pack identifier (slug)." },
            ["name"] = new JsonObject { ["type"] = "string" },
            ["version"] = new JsonObject { ["type"] = "string" },
            ["author"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = "string" },
            ["output"] = new JsonObject { ["type"] = "string", ["description"] = "Output file path. If absent, returns pack data." },
            ["types"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["packages"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray { "pack_id" },
        };
    }
}
