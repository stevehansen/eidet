using System.Net;
using Eidet.Core.Services;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoints over <see cref="UsageTracker"/>: aggregate report, per-operation
/// time series, and hourly buckets. Each route 503s when usage tracking is disabled.
/// </summary>
internal sealed class UsageEndpoints
{
    private readonly UsageTracker? _usage;

    public UsageEndpoints(UsageTracker? usage) => _usage = usage;

    public async Task Usage(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_usage is null) { await HttpJson.WriteAsync(ctx, new { error = "Usage tracking not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        var days = int.TryParse(ctx.Request.QueryString["days"], out var d) ? d : 30;
        var report = await _usage.GetUsageAsync(repo, DateTime.UtcNow.AddDays(-days));
        await HttpJson.WriteAsync(ctx, report);
    }

    public async Task TimeSeries(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_usage is null) { await HttpJson.WriteAsync(ctx, new { error = "Usage tracking not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        var op = ctx.Request.QueryString["operation"];
        if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(op))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' and 'operation' parameters" }, 400);
            return;
        }
        var days = int.TryParse(ctx.Request.QueryString["days"], out var d) ? d : 30;
        var data = await _usage.GetTimeSeriesAsync(repo, op, DateTime.UtcNow.AddDays(-days));
        await HttpJson.WriteAsync(ctx, new { repo, operation = op, data });
    }

    public async Task Hourly(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_usage is null) { await HttpJson.WriteAsync(ctx, new { error = "Usage tracking not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        var days = int.TryParse(ctx.Request.QueryString["days"], out var d) ? d : 7;
        var buckets = await _usage.GetHourlyBreakdownAsync(repo, days);
        await HttpJson.WriteAsync(ctx, new { repo, days, buckets });
    }
}
