using Eidet.Core.Domain;

namespace Eidet.Core.Portal.Sections;

/// <summary>
/// All Heuristic-typed memories ordered by Importance desc.
/// Omitted when no heuristics exist.
/// </summary>
internal sealed class HeuristicsSection : IPortalSection
{
    public string Id => "heuristics";
    public string Title => "Rules of Thumb";
    public bool AlwaysPresent => false;

    public Task<PortalSection?> RenderAsync(PortalContext ctx, CancellationToken ct)
    {
        var items = PortalMarkup
            .ByImportanceThenId(ctx.AllValidMemories.Where(m => m.Type == MemoryType.Heuristic))
            .ToList();
        if (items.Count == 0) return Task.FromResult<PortalSection?>(null);

        return Task.FromResult<PortalSection?>(new PortalSection(
            Id, Title,
            PortalMarkup.UnorderedList(items),
            items.Select(m => m.Id).ToList()));
    }
}
