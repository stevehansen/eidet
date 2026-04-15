using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

/// <summary>
/// Synchronizes a .eidet pack file with a mounted layer.
/// Diffs pack entries against stored layer entries, then adds/updates/removes as needed.
/// Designed for the git-repo-as-layer workflow where packs are versioned externally.
/// </summary>
public class LayerSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IEidetStore _store;
    private readonly LayerService _layers;

    public LayerSyncService(IEidetStore store, LayerService layers)
    {
        _store = store;
        _layers = layers;
    }

    /// <summary>
    /// Load a pack from disk and preview what a sync would do, without applying changes.
    /// </summary>
    public async Task<LayerSyncPreview> PreviewAsync(string packPath, string? layerId = null, CancellationToken ct = default)
    {
        var pack = await LoadPackAsync(packPath, ct);
        layerId ??= $"bundle:{pack.Id}";
        return await DiffAsync(pack, layerId, ct);
    }

    /// <summary>
    /// Load a pack from disk and sync it into the layer, applying all changes.
    /// Creates the layer if it doesn't exist. Updates version on completion.
    /// </summary>
    public async Task<LayerSyncResult> SyncAsync(
        string packPath, string? layerId = null, bool removeStale = true, CancellationToken ct = default)
    {
        var pack = await LoadPackAsync(packPath, ct);
        layerId ??= $"bundle:{pack.Id}";

        var preview = await DiffAsync(pack, layerId, ct);
        var packEntryMap = pack.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        // Apply additions
        foreach (var entry in preview.Entries.Where(e => e.Action == SyncAction.Add))
        {
            var packEntry = packEntryMap[entry.Id];
            packEntry.LayerId = layerId;
            await _store.StoreAsync(packEntry, ct);
        }

        // Apply updates (overwrite stored entry with pack version)
        foreach (var entry in preview.Entries.Where(e => e.Action == SyncAction.Update))
        {
            var packEntry = packEntryMap[entry.Id];
            packEntry.LayerId = layerId;
            await _store.UpdateAsync(packEntry, ct);
        }

        // Apply removals
        if (removeStale)
        {
            foreach (var entry in preview.Entries.Where(e => e.Action == SyncAction.Remove))
                await _store.HardDeleteAsync(entry.Id, ct);
        }

        // Ensure layer is mounted, update its version
        var layer = await _store.GetLayerAsync(layerId, ct);
        if (layer is null)
        {
            await _layers.MountAsync(layerId, pack.Name, LayerType.Base,
                applicablePackages: pack.ApplicablePackages,
                sourcePath: packPath,
                version: pack.Version,
                ct: ct);
        }
        else
        {
            layer.Version = pack.Version;
            layer.LastSyncedAt = DateTime.UtcNow;
            layer.SourcePath = packPath;
            await _store.StoreMountedLayerAsync(layer, ct);
        }

        return new LayerSyncResult
        {
            LayerId = layerId,
            PackName = pack.Name,
            PackVersion = pack.Version,
            Added = preview.Added,
            Updated = preview.Updated,
            Removed = removeStale ? preview.Removed : 0,
            Unchanged = preview.Unchanged,
            StaleKept = removeStale ? 0 : preview.Removed,
        };
    }

    private async Task<LayerSyncPreview> DiffAsync(EidetPack pack, string layerId, CancellationToken ct)
    {
        var currentEntries = await _store.GetByLayerIdAsync(layerId, ct);
        var currentMap = currentEntries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var packMap = pack.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var entries = new List<SyncEntryPreview>();

        // Check each pack entry against current state
        foreach (var packEntry in pack.Entries)
        {
            if (currentMap.TryGetValue(packEntry.Id, out var existing))
            {
                if (ContentEquals(packEntry, existing))
                {
                    entries.Add(new SyncEntryPreview
                    {
                        Id = packEntry.Id,
                        OneLiner = packEntry.OneLiner ?? StringUtils.Truncate(packEntry.Content, 60),
                        Type = packEntry.Type,
                        Action = SyncAction.Unchanged,
                    });
                }
                else
                {
                    entries.Add(new SyncEntryPreview
                    {
                        Id = packEntry.Id,
                        OneLiner = packEntry.OneLiner ?? StringUtils.Truncate(packEntry.Content, 60),
                        Type = packEntry.Type,
                        Action = SyncAction.Update,
                    });
                }
            }
            else
            {
                entries.Add(new SyncEntryPreview
                {
                    Id = packEntry.Id,
                    OneLiner = packEntry.OneLiner ?? StringUtils.Truncate(packEntry.Content, 60),
                    Type = packEntry.Type,
                    Action = SyncAction.Add,
                });
            }
        }

        // Check for entries in store but not in pack (removals)
        foreach (var existing in currentEntries)
        {
            if (!packMap.ContainsKey(existing.Id))
            {
                entries.Add(new SyncEntryPreview
                {
                    Id = existing.Id,
                    OneLiner = existing.OneLiner ?? StringUtils.Truncate(existing.Content, 60),
                    Type = existing.Type,
                    Action = SyncAction.Remove,
                });
            }
        }

        return new LayerSyncPreview
        {
            LayerId = layerId,
            PackName = pack.Name,
            PackVersion = pack.Version,
            CurrentVersion = (await _store.GetLayerAsync(layerId, ct))?.Version,
            Added = entries.Count(e => e.Action == SyncAction.Add),
            Updated = entries.Count(e => e.Action == SyncAction.Update),
            Removed = entries.Count(e => e.Action == SyncAction.Remove),
            Unchanged = entries.Count(e => e.Action == SyncAction.Unchanged),
            Entries = entries,
        };
    }

    /// <summary>
    /// Compare content and key metadata fields to detect changes.
    /// </summary>
    internal static bool ContentEquals(MemoryEntry a, MemoryEntry b) =>
        string.Equals(a.Content, b.Content, StringComparison.Ordinal) &&
        string.Equals(a.OneLiner, b.OneLiner, StringComparison.Ordinal) &&
        string.Equals(a.Summary, b.Summary, StringComparison.Ordinal) &&
        a.Type == b.Type &&
        a.Importance == b.Importance &&
        TagsEqual(a.Tags, b.Tags);

    private static bool TagsEqual(List<string> a, List<string> b) =>
        a.Count == b.Count && a.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(b.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static async Task<EidetPack> LoadPackAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Pack file not found", path);

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<EidetPack>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse pack file: {path}");
    }
}

// ─── Domain types for sync ──────────────────────────────────────────

public class LayerSyncPreview
{
    public string LayerId { get; set; } = "";
    public string PackName { get; set; } = "";
    public string PackVersion { get; set; } = "";
    public string? CurrentVersion { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Unchanged { get; set; }
    public List<SyncEntryPreview> Entries { get; set; } = [];
}

public class LayerSyncResult
{
    public string LayerId { get; set; } = "";
    public string PackName { get; set; } = "";
    public string PackVersion { get; set; } = "";
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Unchanged { get; set; }
    public int StaleKept { get; set; }
}

public class SyncEntryPreview
{
    public string Id { get; set; } = "";
    public string? OneLiner { get; set; }
    public MemoryType Type { get; set; }
    public SyncAction Action { get; set; }
}

public enum SyncAction { Unchanged, Add, Update, Remove }
