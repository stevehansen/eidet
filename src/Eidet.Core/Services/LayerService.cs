using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

/// <summary>
/// Manages memory layers — mount/unmount, scope resolution, auto-mount by dependencies.
/// Layers compose: Local (rw) on top, Shared/Base (ro) below.
/// Writes always go to local layer. Non-local results de-boosted by 0.8×.
/// </summary>
public class LayerService
{
    private readonly IEidetStore _store;

    public LayerService(IEidetStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Mount a layer. For pack imports, creates a Base layer from the .eidet file.
    /// For shared layers, creates a Shared layer pointing to a team config.
    /// </summary>
    public async Task<MemoryLayer> MountAsync(
        string layerId, string name, LayerType type,
        List<string>? applicableRepos = null,
        List<string>? applicablePackages = null,
        string? sourcePath = null,
        CancellationToken ct = default)
    {
        // Check if already mounted
        var existing = await _store.GetLayerAsync(layerId, ct);
        if (existing is not null)
            return existing;

        var layer = new MemoryLayer
        {
            Id = layerId,
            Name = name,
            Type = type,
            ReadOnly = type != LayerType.Local,
            SourcePath = sourcePath,
            MountedAt = DateTime.UtcNow,
            Priority = type switch
            {
                LayerType.Local => 100,
                LayerType.Shared => 50,
                LayerType.Base => 10,
                _ => 10,
            },
            ApplicableRepos = applicableRepos ?? [],
            ApplicablePackages = applicablePackages ?? [],
        };

        await _store.StoreMountedLayerAsync(layer, ct);
        return layer;
    }

    public async Task<bool> UnmountAsync(string layerId, CancellationToken ct = default) =>
        await _store.UnmountLayerAsync(layerId, ct);

    /// <summary>
    /// Get all layers applicable to a repo, ordered by priority (local first).
    /// A layer applies if: ApplicableRepos is empty (universal),
    /// or the repoId is in ApplicableRepos, or the repo depends on a covered package.
    /// </summary>
    public async Task<List<MemoryLayer>> GetApplicableLayersAsync(
        string repoId, List<string>? repoPackages = null, CancellationToken ct = default)
    {
        var layers = await _store.GetMountedLayersAsync(repoId, ct);

        // Also include layers that match by package dependency
        if (repoPackages is { Count: > 0 })
        {
            var allLayers = await _store.GetMountedLayersAsync("", ct); // Get all layers
            foreach (var layer in allLayers)
            {
                if (layers.Any(l => l.Id == layer.Id)) continue;
                if (layer.ApplicablePackages.Any(pkg =>
                    repoPackages.Contains(pkg, StringComparer.OrdinalIgnoreCase)))
                {
                    layers.Add(layer);
                }
            }
        }

        return layers.OrderByDescending(l => l.Priority).ToList();
    }

    /// <summary>
    /// Resolve the full scope of repoIds for recall, including all mounted layers.
    /// Returns the set of repoIds to search across.
    /// </summary>
    public async Task<List<string>> ResolveScopeAsync(
        string repoId, bool crossRepo = true, CancellationToken ct = default)
    {
        var repoIds = new List<string> { repoId };

        if (!crossRepo) return repoIds;

        var layers = await GetApplicableLayersAsync(repoId, ct: ct);
        foreach (var layer in layers)
        {
            // Each layer may have memories stored under different repoIds
            // For base/shared layers, their memories use the layer's source repo
            foreach (var applicableRepo in layer.ApplicableRepos)
            {
                if (!repoIds.Contains(applicableRepo, StringComparer.OrdinalIgnoreCase))
                    repoIds.Add(applicableRepo);
            }
        }

        return repoIds;
    }

    /// <summary>
    /// Auto-mount layers based on detected package dependencies.
    /// Called during intake when dependencies are detected.
    /// </summary>
    public async Task<int> AutoMountByDependenciesAsync(
        string repoId, List<string> packageDependencies, CancellationToken ct = default)
    {
        // Get all available layers (universal scope)
        var allLayers = await _store.GetMountedLayersAsync("", ct);
        var mounted = 0;

        foreach (var layer in allLayers)
        {
            if (layer.ApplicableRepos.Contains(repoId, StringComparer.OrdinalIgnoreCase))
                continue; // Already applies

            // Check if any of the layer's applicable packages match repo dependencies
            if (layer.ApplicablePackages.Any(pkg =>
                packageDependencies.Contains(pkg, StringComparer.OrdinalIgnoreCase)))
            {
                // Add this repo to the layer's applicable repos
                if (!layer.ApplicableRepos.Contains(repoId, StringComparer.OrdinalIgnoreCase))
                {
                    layer.ApplicableRepos.Add(repoId);
                    await _store.StoreMountedLayerAsync(layer, ct);
                    mounted++;
                }
            }
        }

        return mounted;
    }

    /// <summary>
    /// Check if a result is from a non-local layer (should be de-boosted).
    /// </summary>
    public static bool IsNonLocal(MemoryEntry entry, string localRepoId) =>
        !string.IsNullOrEmpty(entry.LayerId) ||
        !string.Equals(entry.RepoId, localRepoId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// De-boost factor for non-local layer results (0.8× per spec).
    /// </summary>
    public const float NonLocalDeBoost = 0.8f;
}
