using System.Net;
using System.Text.Json;
using Eidet.Core.LooseEnds;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// Loose End REST routes: park and resolve route through <see cref="ToolDispatcher"/> for parity
/// with MCP; the open-work pull list calls <see cref="LooseEndService"/> directly (it is the
/// off-MCP human surface). The id in the resolve path is itself slash-bearing
/// (<c>looseends/{repo}/{hash}</c>), so it is carried as the path between the route prefix and
/// the <c>/resolve</c> suffix.
/// </summary>
internal sealed class LooseEndEndpoints
{
    private readonly ToolDispatcher _dispatcher;
    private readonly LooseEndService _looseEnds;

    public LooseEndEndpoints(ToolDispatcher dispatcher, LooseEndService looseEnds)
    {
        _dispatcher = dispatcher;
        _looseEnds = looseEnds;
    }

    public async Task Park(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var req = await HttpJson.ReadAsync<ParkRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Note))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required field: note" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            note = req.Note,
            tags = req.Tags,
            priority = req.Priority ?? 2,
        }, HttpJson.Options);

        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_park", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result, successStatus: 201);
    }

    public async Task Resolve(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid Loose End ID in path" }, 400);
            return;
        }

        var req = await HttpJson.ReadAsync<ResolveRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Kind))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required field: kind" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            id = Uri.UnescapeDataString(id),
            kind = req.Kind,
            note = req.Note,
            promote_type = req.PromoteType,
            promote_to = req.PromoteTo,
        }, HttpJson.Options);

        var repo = ctx.Request.QueryString["repo"] ?? "";
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_resolve", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task List(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var open = await _looseEnds.PullAsync(repo, ct: ct);
        await HttpJson.WriteAsync(ctx, new { repo, state = "open", looseEnds = open });
    }

    /// <summary>
    /// Extract the Loose End ID from paths like <c>/api/eidet/loose-ends/{id}/resolve</c>. The id
    /// itself contains slashes (<c>looseends/{repo}/{hash}</c>), so take everything between the
    /// prefix and the <c>/resolve</c> suffix.
    /// </summary>
    public static string ExtractIdFromResolvePath(string path)
    {
        const string prefix = "/api/eidet/loose-ends/";
        const string suffix = "/resolve";
        if (path.StartsWith(prefix) && path.EndsWith(suffix) && path.Length > prefix.Length + suffix.Length)
            return path[prefix.Length..^suffix.Length];
        return "";
    }

    private sealed record ParkRequest(string? Note, List<string>? Tags, int? Priority);

    private sealed record ResolveRequest(string? Kind, string? Note, string? PromoteType, string? PromoteTo);
}
