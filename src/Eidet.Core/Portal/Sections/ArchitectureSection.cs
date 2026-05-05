using System.Text;
using Eidet.Core.Domain;

namespace Eidet.Core.Portal.Sections;

/// <summary>
/// All Insight-typed memories grouped by primary tag (alphabetically-first tag),
/// sorted within group by Importance desc and across groups by max-Importance member desc.
/// Omitted when no insights exist.
/// </summary>
internal sealed class ArchitectureSection : IPortalSection
{
    public string Id => "architecture";
    public string Title => "Architecture";
    public bool AlwaysPresent => false;

    public Task<PortalSection?> RenderAsync(PortalContext ctx, CancellationToken ct)
    {
        var insights = ctx.AllValidMemories.Where(m => m.Type == MemoryType.Insight).ToList();
        if (insights.Count == 0) return Task.FromResult<PortalSection?>(null);

        var groups = insights
            .GroupBy(PortalMarkup.PrimaryTag)
            .Select(g => new
            {
                Tag = g.Key,
                Members = PortalMarkup.ByImportanceThenId(g).ToList(),
            })
            .OrderByDescending(g => g.Members.Max(m => m.Importance))
            .ThenBy(g => g.Tag, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        var citations = new List<string>();
        foreach (var group in groups)
        {
            sb.Append("<h4 class=\"portal-tag-group\">").Append(PortalMarkup.Esc(group.Tag)).Append("</h4>");
            sb.Append(PortalMarkup.UnorderedList(group.Members));
            citations.AddRange(group.Members.Select(m => m.Id));
        }

        return Task.FromResult<PortalSection?>(new PortalSection(Id, Title, sb.ToString(), citations));
    }
}
