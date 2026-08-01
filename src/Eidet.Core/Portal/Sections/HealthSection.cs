using System.Text;
using Eidet.Core.Domain;

namespace Eidet.Core.Portal.Sections;

/// <summary>
/// Summary metrics derivable from existing data: count by type and a freshness
/// histogram bucketed by <see cref="MemoryEntry.CreatedAt"/> (the only timestamp
/// every memory has). Last-modified and last-consolidation are excluded from v1
/// per docs/domains/portal.md. Always present.
/// </summary>
internal sealed class HealthSection : IPortalSection
{
    public string Id => "health";
    public string Title => "Memory Health";
    public bool AlwaysPresent => true;

    public Task<PortalSection?> RenderAsync(PortalContext ctx, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var memories = ctx.AllValidMemories;

        var sb = new StringBuilder();
        sb.Append("<dl class=\"portal-health\">");

        sb.Append("<dt>Total memories</dt><dd>").Append(memories.Count).Append("</dd>");

        foreach (var kv in ctx.CountsByType.OrderBy(kv => kv.Key))
        {
            sb.Append("<dt>")
              .Append(PortalMarkup.Esc(kv.Key.ToString()))
              .Append("</dt><dd>")
              .Append(kv.Value)
              .Append("</dd>");
        }

        // Freshness histogram by CreatedAt.
        var last7 = memories.Count(m => (now - m.CreatedAt).TotalDays <= 7);
        var last30 = memories.Count(m => (now - m.CreatedAt).TotalDays <= 30);
        var older = memories.Count - last30;
        sb.Append("<dt>Created last 7 days</dt><dd>").Append(last7).Append("</dd>");
        sb.Append("<dt>Created last 30 days</dt><dd>").Append(last30).Append("</dd>");
        sb.Append("<dt>Older than 30 days</dt><dd>").Append(older).Append("</dd>");

        sb.Append("</dl>");

        return Task.FromResult<PortalSection?>(new PortalSection(Id, Title, sb.ToString(), []));
    }
}
