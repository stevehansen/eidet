using System.Net;
using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoints that work directly against memories: store, recall/search, get/update,
/// forget, feedback, history, intake, consolidate, export, packs, links, browse, graph,
/// repos. Routes through <see cref="ToolDispatcher"/> for parity with MCP where the same
/// operation exists, and falls through to <see cref="MemoryService"/> for read-only or
/// curation calls that do not have a tool counterpart.
/// </summary>
internal sealed class MemoryEndpoints
{
    private readonly MemoryService _svc;
    private readonly ToolDispatcher _dispatcher;
    private readonly ExportService _export;
    private readonly UsageTracker? _usage;
    private readonly LayerService? _layers;

    public MemoryEndpoints(MemoryService svc, ToolDispatcher dispatcher, ExportService export,
        UsageTracker? usage, LayerService? layers)
    {
        _svc = svc;
        _dispatcher = dispatcher;
        _export = export;
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
        await HttpJson.WriteAsync(ctx, chain[0]);
    }

    public async Task Store(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await HttpJson.ReadAsync<StoreRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required field: repo" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            content = req.Content,
            type = req.Type.ToString(),
            tags = req.Tags,
            importance = req.Importance,
            source = req.Source,
            sessionId = req.SessionId,
            supersedes = req.Supersedes,
        }, HttpJson.Options);

        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_store", req.Repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result, successStatus: 201);
    }

    public async Task Forget(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            id = Uri.UnescapeDataString(id),
            reason = ctx.Request.QueryString["reason"],
        }, HttpJson.Options);

        var repo = ctx.Request.QueryString["repo"] ?? "";
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_forget", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task Feedback(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await HttpJson.ReadAsync<FeedbackRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.MemoryId))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required field: memoryId" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            id = req.MemoryId,
            used = req.WasUsed,
        }, HttpJson.Options);

        var repo = ExtractRepoFromMemoryId(req.MemoryId) ?? "";
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_feedback", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
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
        var context = await _svc.GetContextAsync(repo, maxTokens: 50, ct: ct);
        await HttpJson.WriteAsync(ctx, new { repo, summary = context.Trim() });
    }

    public async Task Intake(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        // Resolve the filesystem path: use explicit path param, the repo value if it looks
        // like a path, or look up the original path from the usage anchor document.
        var path = ctx.Request.QueryString["path"];
        if (string.IsNullOrEmpty(path))
            path = RepoUsage.LooksLikePath(repo) ? repo : null;
        if (string.IsNullOrEmpty(path) && _usage is not null)
            path = await _usage.GetOriginalPathAsync(repo);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            await HttpJson.WriteAsync(ctx, new { error = $"Cannot resolve filesystem path for repo '{repo}'. The path '{path ?? "(unknown)"}' does not exist." }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new { }, HttpJson.Options);
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_intake", path, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task Consolidate(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var args = JsonDocument.Parse("{}").RootElement;
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_consolidate", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task Export(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        var markdown = await _export.ExportMarkdownAsync(RepoIdNormalizer.Normalize(repo), ct);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/markdown";
        var bytes = System.Text.Encoding.UTF8.GetBytes(markdown);
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    public async Task PackExport(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await HttpJson.ReadAsync<PackExportRequest>(ctx);
        var packId = req?.ResolvedPackId ?? "";
        if (req is null || string.IsNullOrEmpty(req.Repo) || string.IsNullOrEmpty(packId)
            || string.IsNullOrEmpty(req.Name) || string.IsNullOrEmpty(req.Version))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required fields: repo, packId, name, version" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            pack_id = packId,
            name = req.Name,
            version = req.Version,
            author = "user",
            output = req.OutputPath,
        }, HttpJson.Options);

        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_pack_export", req.Repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task PackImport(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await HttpJson.ReadAsync<PackImportRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Path))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required field: path" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new { path = req.Path }, HttpJson.Options);
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_pack_import", "", args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task CreateLink(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await HttpJson.ReadAsync<CreateLinkRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Repo) || string.IsNullOrEmpty(req.TargetRepo)
            || string.IsNullOrEmpty(req.Relation))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required fields: repo, targetRepo, relation" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            target_repo = req.TargetRepo,
            relation = req.Relation,
            source = "user",
        }, HttpJson.Options);

        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_link", req.Repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result, successStatus: 201);
    }

    public async Task GetLinks(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var query = new MemoryQuery
        {
            Text = "cross-repo link",
            Tags = ["cross-repo-link"],
            Limit = 50,
        };
        var results = await _svc.RecallAsync(repo, query, ct);
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

        // Gather cross-repo scope info
        List<object>? layerInfo = null;
        List<string>? scope = null;
        if (_layers is not null)
        {
            var normalizedRepoId = RepoIdNormalizer.Normalize(repo);
            var layers = await _layers.GetApplicableLayersAsync(normalizedRepoId, ct: ct);
            layerInfo = layers.Select(l => (object)new { l.Id, l.Name, type = l.Type.ToString() }).ToList();
            scope = await _layers.ResolveScopeAsync(normalizedRepoId, crossRepo: true, ct: ct);
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

    public async Task UpdateMemory(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        var decoded = Uri.UnescapeDataString(id);
        var req = await HttpJson.ReadAsync<UpdateMemoryRequest>(ctx);
        if (req is null)
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid request body" }, 400);
            return;
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            id = decoded,
            content = req.Content,
            tags = req.Tags,
            importance = req.Importance,
            confidence = req.Confidence,
            type = req.Type,
            oneLiner = req.OneLiner,
            summary = req.Summary,
            foresightHint = req.ForesightHint,
        }, HttpJson.Options);

        var repo = ExtractRepoFromMemoryId(decoded) ?? "";
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_edit", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task AddMemoryLink(HttpListenerContext ctx, string memoryId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(memoryId))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid memory ID in path" }, 400);
            return;
        }
        var decoded = Uri.UnescapeDataString(memoryId);
        var req = await HttpJson.ReadAsync<AddMemoryLinkRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.TargetRepoId) || string.IsNullOrEmpty(req.Relation))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required fields: targetRepoId, relation" }, 400);
            return;
        }

        var ok = await _svc.AddLinkAsync(decoded, req.TargetRepoId, req.Relation, req.TargetMemoryId, ct);
        if (ok) await HttpJson.WriteAsync(ctx, new { linked = true }, 201);
        else await HttpJson.WriteAsync(ctx, new { error = "Memory not found" }, 404);
    }

    public async Task RemoveMemoryLink(HttpListenerContext ctx, string memoryId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(memoryId))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid memory ID in path" }, 400);
            return;
        }
        var decoded = Uri.UnescapeDataString(memoryId);
        var targetRepo = ctx.Request.QueryString["targetRepoId"];
        var relation = ctx.Request.QueryString["relation"];
        if (string.IsNullOrEmpty(targetRepo) || string.IsNullOrEmpty(relation))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing query params: targetRepoId, relation" }, 400);
            return;
        }

        var ok = await _svc.RemoveLinkAsync(decoded, targetRepo, relation, ct);
        if (ok) await HttpJson.WriteAsync(ctx, new { removed = true });
        else await HttpJson.WriteAsync(ctx, new { error = "Link or memory not found" }, 404);
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

    /// <summary>
    /// Extract memory ID from paths like /api/eidet/{memoryId}/links.
    /// Memory IDs contain slashes (memories/repoSlug/type/hash), so we take everything between /api/eidet/ and /links.
    /// </summary>
    public static string ExtractMemoryIdFromLinkPath(string path)
    {
        const string prefix = "/api/eidet/";
        const string suffix = "/links";
        if (path.StartsWith(prefix) && path.EndsWith(suffix) && path.Length > prefix.Length + suffix.Length)
            return path[prefix.Length..^suffix.Length];
        return "";
    }

    private static string? ExtractRepoFromMemoryId(string memoryId)
    {
        // Memory IDs follow: memories/{repoSlug}/{type}/{hash}
        if (!memoryId.StartsWith("memories/", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = memoryId.Split('/');
        return parts.Length >= 3 ? parts[1].Replace("--", ":\\").Replace('-', '\\') : null;
    }
}
