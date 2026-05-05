using System.Text;
using Eidet.Core.Domain;

namespace Eidet.Core.Portal.Sections;

/// <summary>
/// Composes the Identity paragraph from a 4-step precedence (PortalSpec.md
/// §Page Structure):
///   1. Memory tagged <c>portal:identity</c>.
///   2. Top 3 insights by importance.
///   3. Intake-derived insights from README/CLAUDE.md.
///   4. Count-only fallback when no source memory exists yet.
/// First match wins; later steps are not evaluated. Always present.
/// </summary>
internal sealed class IdentitySection : IPortalSection
{
    public string Id => "identity";
    public string Title => "Identity";
    public bool AlwaysPresent => true;

    public Task<PortalSection?> RenderAsync(PortalContext ctx, CancellationToken ct)
    {
        // Step 1: curated identity memory wins outright.
        var curated = ctx.AllValidMemories
            .FirstOrDefault(m => m.Tags.Any(t => string.Equals(t, "portal:identity", StringComparison.OrdinalIgnoreCase)));
        if (curated is not null)
            return Done("identity-curated", $"<p>{PortalMarkup.Cite(curated)}</p>", [curated.Id]);

        // Step 2: top 3 insights by Importance.
        var topInsights = PortalMarkup
            .ByImportanceThenId(ctx.AllValidMemories.Where(m => m.Type == MemoryType.Insight))
            .Take(3)
            .ToList();
        if (topInsights.Count > 0)
        {
            var sb = new StringBuilder("<p class=\"portal-identity\">");
            for (var i = 0; i < topInsights.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(PortalMarkup.Cite(topInsights[i])).Append('.');
            }
            sb.Append("</p>");
            return Done("identity-top-insights", sb.ToString(), topInsights.Select(m => m.Id).ToList());
        }

        // Step 3: README/CLAUDE intake-derived insights.
        var intakeInsights = PortalMarkup
            .ByImportanceThenId(ctx.AllValidMemories
                .Where(m => m.Type == MemoryType.Insight && m.Provenance == MemoryProvenance.Intake))
            .Take(3)
            .ToList();
        if (intakeInsights.Count > 0)
        {
            var sb = new StringBuilder("<p class=\"portal-identity\">");
            for (var i = 0; i < intakeInsights.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(PortalMarkup.Cite(intakeInsights[i])).Append('.');
            }
            sb.Append("</p>");
            return Done("identity-intake", sb.ToString(), intakeInsights.Select(m => m.Id).ToList());
        }

        // Step 4: count-only fallback.
        var total = ctx.CountsByType.Values.Sum();
        var byType = string.Join(", ", ctx.CountsByType
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Value} {kv.Key.ToString().ToLowerInvariant()}{(kv.Value == 1 ? "" : "s")}"));
        var summary = total == 0
            ? "No memories yet. Run <code>eidet intake</code> against this repo's README and CLAUDE.md to seed an identity paragraph."
            : $"Eidet sees {PortalMarkup.Esc(byType)} for this repo. Tag a memory <code>portal:identity</code> to curate this paragraph.";
        return Done("identity-stub", $"<p class=\"portal-identity portal-stub\">{summary}</p>", []);
    }

    private static Task<PortalSection?> Done(string variantId, string html, IReadOnlyList<string> citations) =>
        Task.FromResult<PortalSection?>(new PortalSection("identity", "Identity", html, citations));
}
