using System.Text.Json.Nodes;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Imports a memory pack file. If <see cref="LayerService"/> is available, mounts the pack as a
/// read-only layer; otherwise imports entries inline.
/// </summary>
public sealed class PackImportToolHandler : IToolHandler
{
    private readonly ExportService? _export;
    private readonly LayerService? _layers;

    public PackImportToolHandler(ExportService? export, LayerService? layers)
    {
        _export = export;
        _layers = layers;
    }

    public string Name => "eidet_pack_import";
    public string UsageOp => "PackImport";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_pack_import",
        Description = "Import a memory pack file. Auto-mounts as a layer when layer service is available.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        if (_export == null)
            return ToolResult.Internal("Pack import not available in this context.");

        var path = ToolArgs.RequireString(request.Arguments, "path");
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(request.RepoId, path);

        if (!File.Exists(resolvedPath))
            return ToolResult.NotFound($"File not found: {resolvedPath}");

        var pack = await _export.ImportPackFromFileAsync(resolvedPath, request.Ct);

        if (_layers != null)
        {
            var (imported, layer) = await _export.ImportPackWithLayerAsync(pack, _layers, request.Ct);
            return ToolResult.Ok(
                payload: new { imported, pack = pack.Name, version = pack.Version, layer = layer.Name },
                summary: $"Imported {imported} memories from \"{pack.Name}\" v{pack.Version}. Mounted as layer: {layer.Name}",
                count: imported);
        }

        var count = await _export.ImportPackAsync(pack, request.Ct);
        return ToolResult.Ok(
            payload: new { imported = count, bundle = pack.Name, version = pack.Version },
            summary: $"Imported {count} memories from \"{pack.Name}\" v{pack.Version}.",
            count: count);
    }

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to the pack file (markdown or JSON).",
            },
        },
        ["required"] = new JsonArray { "path" },
    };
}
