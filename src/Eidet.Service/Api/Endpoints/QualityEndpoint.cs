using System.Net;
using Eidet.Core.Services;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoint for the per-repo quality report. Returns 503 when the
/// quality service is not configured.
/// </summary>
internal sealed class QualityEndpoint
{
    private readonly QualityService? _quality;
    private readonly UsageTracker? _usage;

    public QualityEndpoint(QualityService? quality, UsageTracker? usage)
    {
        _quality = quality;
        _usage = usage;
    }

    public async Task Quality(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_quality is null) { await HttpJson.WriteAsync(ctx, new { error = "Quality service not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        using var scope = _usage?.StartScope(repo, "Quality");
        var report = await _quality.AnalyzeAsync(repo, ct);
        await HttpJson.WriteAsync(ctx, report);
    }
}
