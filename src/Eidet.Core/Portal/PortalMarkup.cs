using System.Net;
using System.Text;
using Eidet.Core.Domain;

namespace Eidet.Core.Portal;

/// <summary>
/// HTML fragment helpers shared by all <see cref="IPortalSection"/> implementations.
/// Citations render as anchors targeting the SPA's <c>#memory/&lt;id&gt;</c> hash route;
/// hover-card data is carried on <c>data-mid</c> so the client can fetch fresh on hover.
/// </summary>
internal static class PortalMarkup
{
    public static string Esc(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    /// <summary>
    /// Renders a memory's display label (OneLiner → Summary → truncated Content)
    /// as an inline citation hyperlink to <c>#memory/&lt;encoded-id&gt;</c>.
    /// </summary>
    public static string Cite(MemoryEntry m)
    {
        var label = m.OneLiner ?? m.Summary ?? Truncate(m.Content, 120);
        var href = "#memory/" + Uri.EscapeDataString(m.Id);
        return $"<a class=\"portal-cite\" href=\"{Esc(href)}\" data-mid=\"{Esc(m.Id)}\">{Esc(label)}</a>";
    }

    public static string Bullet(MemoryEntry m) => $"<li>{Cite(m)}</li>";

    public static string UnorderedList(IEnumerable<MemoryEntry> items)
    {
        var sb = new StringBuilder("<ul class=\"portal-list\">");
        foreach (var m in items) sb.Append(Bullet(m));
        sb.Append("</ul>");
        return sb.ToString();
    }

    public static string OrderedList(IEnumerable<MemoryEntry> items)
    {
        var sb = new StringBuilder("<ol class=\"portal-list\">");
        foreach (var m in items) sb.Append(Bullet(m));
        sb.Append("</ol>");
        return sb.ToString();
    }

    public static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Stable secondary sort key for sections: importance descending, then Id ascending.
    /// Mirrors the shared section ordering (docs/domains/portal.md).
    /// </summary>
    public static IEnumerable<MemoryEntry> ByImportanceThenId(IEnumerable<MemoryEntry> items) =>
        items.OrderByDescending(m => m.Importance).ThenBy(m => m.Id, StringComparer.Ordinal);

    public static string PrimaryTag(MemoryEntry m) =>
        m.Tags.Count == 0 ? "(untagged)" : m.Tags.OrderBy(t => t, StringComparer.Ordinal).First();
}
