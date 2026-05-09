using Eidet.Core.Domain;
using Eidet.Core.Layers;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

/// <summary>
/// Synchronises a pack with a mounted layer.
/// Diffs pack entries against stored layer entries, then adds/updates/removes as needed.
/// Designed for the git-repo-as-layer workflow where packs are versioned externally.
/// </summary>
/// <remarks>
/// Pack loading is delegated to an <see cref="ILayerSource"/> resolved by
/// <see cref="LayerSourceRef.Scheme"/>; the default registry maps <c>file</c> to
/// <see cref="FilesystemLayerSource"/>. Plug a new transport in by registering an
/// extra source with the constructor.
/// </remarks>
public class LayerSyncService
{
    private readonly IEidetStore _store;
    private readonly LayerService _layers;
    private readonly MemoryService? _memory;
    private readonly Dictionary<string, ILayerSource> _sources;

    public LayerSyncService(
        IEidetStore store, LayerService layers,
        IEnumerable<ILayerSource>? sources = null, MemoryService? memory = null)
    {
        _store = store;
        _layers = layers;
        _memory = memory;
        _sources = (sources ?? [new FilesystemLayerSource()])
            .ToDictionary(s => s.Scheme, StringComparer.OrdinalIgnoreCase);
    }

    private ILayerSource ResolveSource(LayerSourceRef r) =>
        _sources.TryGetValue(r.Scheme, out var src)
            ? src
            : throw new InvalidOperationException($"No ILayerSource registered for scheme '{r.Scheme}'");

    // Prefer the canonical "pack:" layer ID for new mounts, but reuse a legacy
    // "bundle:" mount if one already exists (pre-Pack-rename imports).
    private async Task<string> ResolveDefaultLayerIdAsync(string packId, CancellationToken ct)
    {
        var legacy = $"bundle:{packId}";
        return await _store.GetLayerAsync(legacy, ct) is not null ? legacy : $"pack:{packId}";
    }

    /// <summary>
    /// Load a pack via the matching <see cref="ILayerSource"/> and preview what a sync would do.
    /// </summary>
    public async Task<LayerSyncPreview> PreviewAsync(LayerSourceRef source, string? layerId = null, CancellationToken ct = default)
    {
        var pack = await ResolveSource(source).LoadAsync(source, ct);
        layerId ??= await ResolveDefaultLayerIdAsync(pack.Id, ct);
        return await DiffAsync(pack, layerId, ct);
    }

    /// <summary>
    /// Convenience wrapper for filesystem packs — equivalent to
    /// <see cref="PreviewAsync(LayerSourceRef, string?, CancellationToken)"/> with
    /// <see cref="LayerSourceRef.File"/>.
    /// </summary>
    public Task<LayerSyncPreview> PreviewAsync(string packPath, string? layerId = null, CancellationToken ct = default) =>
        PreviewAsync(LayerSourceRef.File(packPath), layerId, ct);

    /// <summary>
    /// Convenience wrapper for filesystem packs — equivalent to
    /// <see cref="SyncAsync(LayerSourceRef, string?, bool, CancellationToken)"/> with
    /// <see cref="LayerSourceRef.File"/>.
    /// </summary>
    public Task<LayerSyncResult> SyncAsync(
        string packPath, string? layerId = null, bool removeStale = true, CancellationToken ct = default) =>
        SyncAsync(LayerSourceRef.File(packPath), layerId, removeStale, ct);

    /// <summary>
    /// Load a pack via the matching <see cref="ILayerSource"/> and sync it into the layer.
    /// Creates the layer if it doesn't exist. Updates version on completion.
    /// </summary>
    public async Task<LayerSyncResult> SyncAsync(
        LayerSourceRef source, string? layerId = null, bool removeStale = true, CancellationToken ct = default)
    {
        var pack = await ResolveSource(source).LoadAsync(source, ct);
        layerId ??= await ResolveDefaultLayerIdAsync(pack.Id, ct);

        var preview = await DiffAsync(pack, layerId, ct);
        var packEntryMap = pack.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var touchedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Apply additions
        foreach (var entry in preview.Entries.Where(e => e.Action == SyncAction.Add))
        {
            var packEntry = packEntryMap[entry.Id];
            packEntry.LayerId = layerId;
            await _store.StoreAsync(packEntry, ct);
            touchedScopes.Add(packEntry.RepoId);
        }

        // Apply updates (overwrite stored entry with pack version)
        foreach (var entry in preview.Entries.Where(e => e.Action == SyncAction.Update))
        {
            var packEntry = packEntryMap[entry.Id];
            packEntry.LayerId = layerId;
            await _store.UpdateAsync(packEntry, ct);
            touchedScopes.Add(packEntry.RepoId);
        }

        // Apply removals
        if (removeStale)
        {
            foreach (var entry in preview.Entries.Where(e => e.Action == SyncAction.Remove))
            {
                await _store.HardDeleteAsync(entry.Id, ct);
                // The entry's RepoId is captured by its layer membership; invalidating the
                // layerId-as-scope is sufficient because recall scopes that include this layer
                // resolve via LayerService and will track the layer's bumped generation.
                touchedScopes.Add(layerId);
            }
        }

        // PHASE-2: migrate onto MemoryService gate — see #10. Layer sync touches many entries
        // across potentially many repos; one invalidation per affected scope keeps the recall
        // cache coherent without firing per-entry hooks.
        _memory?.InvalidateRecallCache(touchedScopes);

        // Ensure layer is mounted, update its version
        var layer = await _store.GetLayerAsync(layerId, ct);
        if (layer is null)
        {
            await _layers.MountAsync(layerId, pack.Name, LayerType.Base,
                applicablePackages: pack.ApplicablePackages,
                sourcePath: source.Location,
                version: pack.Version,
                ct: ct);
        }
        else
        {
            layer.Version = pack.Version;
            layer.LastSyncedAt = DateTime.UtcNow;
            layer.SourcePath = source.Location;
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
