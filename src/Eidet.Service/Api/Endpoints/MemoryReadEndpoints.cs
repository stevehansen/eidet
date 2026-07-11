using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// Read-only memory routes: context, search/recall, single get, history, stats, links list,
/// context preview, repos list, browse, graph. Routes through <see cref="ToolDispatcher"/>
/// where a tool counterpart exists, otherwise calls <see cref="MemoryService"/> directly.
/// </summary>
internal sealed class MemoryReadEndpoints
{
    private readonly MemoryService _svc;
    private readonly ToolDispatcher _dispatcher;
    private readonly UsageTracker? _usage;
    private readonly LayerService? _layers;

    public MemoryReadEndpoints(MemoryService svc, ToolDispatcher dispatcher,
        UsageTracker? usage, LayerService? layers)
    {
        _svc = svc;
        _dispatcher = dispatcher;
        _usage = usage;
        _layers = layers;
    }

    public async Task GetContext(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var args = JsonDocument.Parse("{}").RootElement;
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_context", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task Search(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        var q = ctx.Request.QueryString["q"];
        if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(q))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' and 'q' parameters" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            query = q,
            limit = int.TryParse(ctx.Request.QueryString["limit"], out var lim) ? lim : 10,
            type = ctx.Request.QueryString["type"],
            valence = ctx.Request.QueryString["valence"],
            stage = ctx.Request.QueryString["stage"],
            tags = ctx.Request.QueryString["tags"]?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToArray() ?? [],
            cross_repo = string.Equals(ctx.Request.QueryString["cross_repo"], "true", StringComparison.OrdinalIgnoreCase),
        }, HttpJson.Options);

        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_recall", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task GetMemory(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        var decoded = Uri.UnescapeDataString(id);
        var chain = await _svc.GetVersionChainAsync(decoded, ct);
        if (chain.Count == 0)
        {
            await HttpJson.WriteAsync(ctx, new { error = "Memory not found" }, 404);
            return;
        }
        // Additive: expose contentSha256 (#65) alongside the entry fields so a caller can round-trip it
        // as the If-Match precondition on a subsequent PUT without recomputing it locally.
        var node = JsonSerializer.SerializeToNode(chain[0], HttpJson.Options)!.AsObject();
        node["contentSha256"] = Eidet.Core.Domain.ContentHash.Of(chain[0].Content);
        await HttpJson.WriteAsync(ctx, node);
    }

    public async Task History(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToElement(new { id = Uri.UnescapeDataString(id) }, HttpJson.Options);
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_history", "", args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task Stats(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        var normalizedRepoId = RepoIdNormalizer.Normalize(repo);
        var context = await _svc.GetContextAsync(repo, maxTokens: 50, ct: ct);
        var rawCounts = await _svc.GetCountsByTypeAsync(normalizedRepoId, ct);
        var counts = rawCounts.ToDictionary(kv => kv.Key.ToString().ToLowerInvariant(), kv => kv.Value);
        var total = rawCounts.Values.Sum();
        await HttpJson.WriteAsync(ctx, new { repo, summary = context.Trim(), counts, total });
    }

    public async Task GetLinks(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var results = await _svc.RecallAsync(repo, new RecallOptions("cross-repo link")
        {
            Tags = ["cross-repo-link"],
            Limit = 50,
        }, ct);
        await HttpJson.WriteAsync(ctx, new { repo, links = results });
    }

    public async Task ContextPreview(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var maxTokens = int.TryParse(ctx.Request.QueryString["tokens"], out var t) ? t : 600;
        var contextText = await _svc.GetContextAsync(repo, maxTokens, ct);

        List<object>? layerInfo = null;
        IReadOnlyList<string>? scope = null;
        if (_layers is not null)
        {
            var normalizedRepoId = RepoIdNormalizer.Normalize(repo);
            var resolved = await _layers.ResolveScopeAsync(normalizedRepoId, crossRepo: true, ct: ct);
            layerInfo = resolved.MountedLayers.Select(l => (object)new { l.Id, l.Name, type = l.Type.ToString() }).ToList();
            scope = resolved.RepoIds;
        }

        await HttpJson.WriteAsync(ctx, new
        {
            repo,
            maxTokens,
            context = contextText.Trim(),
            estimatedTokens = (int)Math.Ceiling(contextText.Length / 4.0),
            layers = layerInfo,
            crossRepoScope = scope,
        });
    }

    public async Task GetRepos(HttpListenerContext ctx, CancellationToken ct)
    {
        var repos = await _svc.GetRepoIdsAsync(ct);
        var pathMap = _usage is not null
            ? await _usage.GetAllRepoPathsAsync()
            : new Dictionary<string, string?>();
        await HttpJson.WriteAsync(ctx, new
        {
            repos = repos.Select(r => new
            {
                repoId = r,
                originalPath = pathMap.TryGetValue(r, out var p) ? p : null,
            })
        });
    }

    public async Task Browse(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        var skip = int.TryParse(ctx.Request.QueryString["skip"], out var s) ? s : 0;
        var take = int.TryParse(ctx.Request.QueryString["take"], out var t) ? t : 50;
        var type = Enum.TryParse<MemoryType>(ctx.Request.QueryString["type"], true, out var mt) ? mt : (MemoryType?)null;

        using var scope = _usage?.StartScope(repo, "Browse");
        var entries = await _svc.BrowseAsync(repo, skip, take, type, ct);
        scope?.SetResultCount(entries.Count);
        await HttpJson.WriteAsync(ctx, new { repo, skip, take, count = entries.Count, entries });
    }

    public async Task Graph(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        var limit = int.TryParse(ctx.Request.QueryString["limit"], out var lim) ? lim : 200;
        using var scope = _usage?.StartScope(repo, "Graph");
        var graph = await _svc.GetGraphDataAsync(repo, limit, ct);
        scope?.SetResultCount(graph.Nodes.Count);
        await HttpJson.WriteAsync(ctx, graph);
    }
}
