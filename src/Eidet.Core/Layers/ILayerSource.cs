using Eidet.Core.Domain;

namespace Eidet.Core.Layers;

/// <summary>
/// Port for loading layer packs from a transport (filesystem, HTTP registry, team share).
/// Implementations are registered by <see cref="Scheme"/> and resolved by
/// <see cref="LayerSourceRef.Scheme"/>; <see cref="Services.LayerSyncService"/> is the
/// only consumer today.
/// </summary>
public interface ILayerSource
{
    /// <summary>Scheme identifier — matches <see cref="LayerSourceRef.Scheme"/> (e.g. "file", "http").</summary>
    string Scheme { get; }

    /// <summary>Load the pack pointed to by <paramref name="r"/>.</summary>
    Task<EidetPack> LoadAsync(LayerSourceRef r, CancellationToken ct);

    /// <summary>
    /// Optional latest-version probe — returns null if the source can't tell, or
    /// if the ref already pins a version.
    /// </summary>
    Task<string?> ResolveLatestVersionAsync(LayerSourceRef r, CancellationToken ct);
}
