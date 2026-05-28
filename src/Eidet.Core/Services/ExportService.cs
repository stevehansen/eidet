using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

public class ExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IEidetStore _store;
    private readonly MemoryService _memory;

    public ExportService(IEidetStore store, MemoryService memory)
    {
        _store = store;
        _memory = memory;
    }

    // ─── Markdown Export ─────────────────────────────────────────────────

    public async Task<string> ExportMarkdownAsync(string repoId, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Eidet Memory Export — {repoId}");
        sb.AppendLine($"Exported: {DateTime.UtcNow:u}");
        sb.AppendLine();

        foreach (var type in Enum.GetValues<MemoryType>())
        {
            var entries = await _store.GetTopScoredAsync(repoId, [type], 200, ct);
            if (entries.Count == 0) continue;

            sb.AppendLine($"## {type}s ({entries.Count})");
            sb.AppendLine();

            foreach (var e in entries.OrderByDescending(e => e.Importance))
            {
                var tags = e.Tags.Count > 0 ? $" `{string.Join("` `", e.Tags)}`" : "";
                sb.AppendLine($"### [{e.Importance:F2}] {e.OneLiner ?? Truncate(e.Content, 60)}");
                sb.AppendLine($"ID: `{e.Id}` | Created: {e.CreatedAt:yyyy-MM-dd}{tags}");
                sb.AppendLine();
                sb.AppendLine(e.Content);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // ─── Pack Export ─────────────────────────────────────────────────────

    public async Task<EidetPack> ExportPackAsync(
        string repoId, string packId, string name, string version, string author,
        List<MemoryType>? types = null, List<string>? tags = null,
        List<string>? applicablePackages = null, CancellationToken ct = default)
    {
        types ??= [MemoryType.Insight, MemoryType.Procedure, MemoryType.Heuristic];

        var entries = await _store.GetTopScoredAsync(repoId, [.. types], 500, ct);

        // Filter by tags if provided
        if (tags is { Count: > 0 })
            entries = entries.Where(e => tags.Any(t => e.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))).ToList();

        // Strip session-specific fields for export
        foreach (var entry in entries)
        {
            entry.AccessCount = 0;
            entry.LastAccessedAt = null;
            entry.SourceSessionId = null;
            entry.EchoCount = 0;
            entry.FizzleCount = 0;
            entry.LayerId = $"pack:{packId}";
        }

        return new EidetPack
        {
            Id = packId,
            Name = name,
            Version = version,
            Author = author,
            CreatedAt = DateTime.UtcNow,
            ApplicablePackages = applicablePackages ?? [],
            Entries = entries,
        };
    }

    public async Task ExportPackToFileAsync(EidetPack pack, string path, CancellationToken ct = default)
    {
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var markdown = MarkdownPackFormat.Serialize(pack);
            await File.WriteAllTextAsync(path, markdown, ct);
        }
        else
        {
            var json = JsonSerializer.Serialize(pack, JsonOptions);
            await File.WriteAllTextAsync(path, json, ct);
        }
    }

    public async Task ExportPackToMarkdownAsync(EidetPack pack, string path, CancellationToken ct = default)
    {
        var markdown = MarkdownPackFormat.Serialize(pack);
        await File.WriteAllTextAsync(path, markdown, ct);
    }

    // ─── Pack Import ─────────────────────────────────────────────────────

    public async Task<EidetPack> ImportPackFromFileAsync(string path, CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(path, ct);

        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return MarkdownPackFormat.Deserialize(content);

        return JsonSerializer.Deserialize<EidetPack>(content, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse pack file: {path}");
    }

    public async Task<EidetPack> ImportPackFromMarkdownAsync(string path, CancellationToken ct = default)
    {
        var markdown = await File.ReadAllTextAsync(path, ct);
        return MarkdownPackFormat.Deserialize(markdown);
    }

    public async Task<int> ImportPackAsync(EidetPack pack, CancellationToken ct = default)
    {
        var result = await _memory.WriteManyAsync(pack.Entries, new BulkWriteOptions { SkipIfExists = true }, ct);
        return result.Added;
    }

    /// <summary>
    /// Import a pack and auto-mount it as a Base layer.
    /// Returns (importedCount, layer).
    /// </summary>
    public async Task<(int Imported, MemoryLayer Layer)> ImportPackWithLayerAsync(
        EidetPack pack, LayerService layerService, CancellationToken ct = default)
    {
        var imported = await ImportPackAsync(pack, ct);

        // Reuse legacy "bundle:" layer if one was mounted before the Pack rename.
        var layerId = $"pack:{pack.Id}";
        var legacyLayerId = $"bundle:{pack.Id}";
        if (await _store.GetLayerAsync(legacyLayerId, ct) is not null)
            layerId = legacyLayerId;

        var layer = await layerService.MountAsync(
            layerId,
            $"{pack.Name} v{pack.Version}",
            LayerType.Base,
            applicablePackages: pack.ApplicablePackages,
            ct: ct);

        return (imported, layer);
    }

    private static string Truncate(string s, int maxLen) =>
        StringUtils.Truncate(s, maxLen);
}
