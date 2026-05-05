namespace Eidet.Core.Portal;

/// <summary>
/// The full Portal payload for a single repo. Returned from
/// <see cref="PortalRenderer.RenderAsync"/> and serialized to the
/// <c>/api/eidet/portal</c> response.
/// </summary>
public sealed record PortalDocument(
    string Repo,
    string Augment,
    PortalStats Stats,
    IReadOnlyList<PortalSection> Sections);

public sealed record PortalSection(
    string Id,
    string Title,
    string Html,
    IReadOnlyList<string> CitedMemoryIds);

public sealed record PortalStats(
    int TotalMemories,
    IReadOnlyDictionary<string, int> ByType);
