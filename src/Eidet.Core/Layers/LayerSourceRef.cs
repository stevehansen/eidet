namespace Eidet.Core.Layers;

/// <summary>
/// Locator for a pack — used by <see cref="ILayerSource"/> adapters to resolve
/// where a pack lives. Today only <c>file</c> is implemented; HTTP / team-share
/// adapters slot in by registering against a different <see cref="Scheme"/>.
/// </summary>
public readonly record struct LayerSourceRef(string Scheme, string Location, string? Version = null)
{
    /// <summary>Build a filesystem reference from a path on disk.</summary>
    public static LayerSourceRef File(string path) => new("file", path);
}
