using Eidet.Core.Domain;
using Eidet.Core.Portal.Sections;
using Eidet.Core.Services;

namespace Eidet.Core.Portal;

/// <summary>
/// Facade that orchestrates Portal rendering. Pre-fetches all currently-valid
/// memories and per-type counts once, then runs each registered
/// <see cref="IPortalSection"/> against the shared <see cref="PortalContext"/>.
/// Sections that return <c>null</c> are omitted from the document.
///
/// v1 is augment=off only — templates render live on every call. Augmented
/// modes (<c>summary</c>, <c>narrative</c>) ship in later phases per
/// PortalSpec.md §Phased Delivery.
/// </summary>
public sealed class PortalRenderer
{
    private const int BrowseTake = 5000;

    private readonly MemoryService _svc;
    private readonly IReadOnlyList<IPortalSection> _sections;

    public PortalRenderer(MemoryService svc)
    {
        _svc = svc;
        _sections = DefaultSections();
    }

    internal PortalRenderer(MemoryService svc, IReadOnlyList<IPortalSection> sections)
    {
        _svc = svc;
        _sections = sections;
    }

    public async Task<PortalDocument> RenderAsync(string repoId, CancellationToken ct = default)
    {
        // BrowseAsync normalizes internally; GetCountsByTypeAsync does not. Normalize once
        // here so both calls see the same key.
        var normalized = RepoIdNormalizer.Normalize(repoId);
        var memories = await _svc.BrowseAsync(normalized, skip: 0, take: BrowseTake, type: null, ct: ct);
        var counts = await _svc.GetCountsByTypeAsync(normalized, ct);

        var pctx = new PortalContext
        {
            RepoId = normalized,
            AllValidMemories = memories,
            CountsByType = counts,
        };

        var sections = new List<PortalSection>(_sections.Count);
        foreach (var s in _sections)
        {
            var rendered = await s.RenderAsync(pctx, ct);
            if (rendered is not null) sections.Add(rendered);
        }

        var stats = new PortalStats(
            memories.Count,
            counts.ToDictionary(kv => kv.Key.ToString().ToLowerInvariant(), kv => kv.Value));

        return new PortalDocument(repoId, "off", stats, sections);
    }

    private static IReadOnlyList<IPortalSection> DefaultSections() =>
    [
        new IdentitySection(),
        new ArchitectureSection(),
        new ProceduresSection(),
        new HeuristicsSection(),
        new HealthSection(),
    ];
}
