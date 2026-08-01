using System.Net;
using Eidet.Core.Portal;
using Eidet.Core.Services;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoint for the per-repo Portal view. v1 supports <c>augment=off</c>
/// only; other levels return 400 until summary/narrative ship per
/// docs/domains/portal.md. The response shape matches
/// <see cref="PortalDocument"/>.
/// </summary>
internal sealed class PortalEndpoint
{
    private readonly PortalRenderer _renderer;
    private readonly UsageTracker? _usage;

    public PortalEndpoint(MemoryService svc, UsageTracker? usage)
    {
        _renderer = new PortalRenderer(svc);
        _usage = usage;
    }

    public async Task Get(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var augment = ctx.Request.QueryString["augment"] ?? "off";
        if (!string.Equals(augment, "off", StringComparison.OrdinalIgnoreCase))
        {
            await HttpJson.WriteAsync(ctx, new
            {
                error = $"Augmentation level '{augment}' not yet supported",
                hint = "v1 supports 'off' only; 'summary' lands in v1.1 and 'narrative' in v1.2.",
            }, 400);
            return;
        }

        using var scope = _usage?.StartScope(repo, "Portal");
        var document = await _renderer.RenderAsync(repo, ct);
        scope?.SetResultCount(document.Sections.Count);
        await HttpJson.WriteAsync(ctx, document);
    }
}
