using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Domain;

namespace Eidet.Core.Layers;

/// <summary>
/// Reads pack JSON files from disk. The default <see cref="ILayerSource"/> implementation —
/// covers the git-repo-as-layer workflow where packs are versioned externally.
/// </summary>
public sealed class FilesystemLayerSource : ILayerSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Scheme => "file";

    public async Task<EidetPack> LoadAsync(LayerSourceRef r, CancellationToken ct)
    {
        if (!File.Exists(r.Location))
            throw new FileNotFoundException("Pack file not found", r.Location);

        var json = await File.ReadAllTextAsync(r.Location, ct);
        return JsonSerializer.Deserialize<EidetPack>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse pack file: {r.Location}");
    }

    public Task<string?> ResolveLatestVersionAsync(LayerSourceRef r, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
