using Eidet.Core.Domain;

namespace Eidet.Core.Portal.Sections;

/// <summary>
/// All Procedure-typed memories ordered by Importance desc.
/// Rendered as a numbered list of OneLiner citations.
/// Omitted when no procedures exist.
/// </summary>
internal sealed class ProceduresSection : IPortalSection
{
    public string Id => "procedures";
    public string Title => "How To";
    public bool AlwaysPresent => false;

    public Task<PortalSection?> RenderAsync(PortalContext ctx, CancellationToken ct)
    {
        var items = PortalMarkup
            .ByImportanceThenId(ctx.AllValidMemories.Where(m => m.Type == MemoryType.Procedure))
            .ToList();
        if (items.Count == 0) return Task.FromResult<PortalSection?>(null);

        return Task.FromResult<PortalSection?>(new PortalSection(
            Id, Title,
            PortalMarkup.OrderedList(items),
            items.Select(m => m.Id).ToList()));
    }
}
