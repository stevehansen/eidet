using System.Net;
using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// Bulk memory routes that operate on whole repos or files: intake, consolidate, markdown
/// export, pack export/import. All route through <see cref="ToolDispatcher"/> for parity
/// with MCP, except markdown export which streams from <see cref="ExportService"/>.
/// </summary>
internal sealed class MemoryBulkEndpoints
{
    private readonly ToolDispatcher _dispatcher;
    private readonly ExportService _export;
    private readonly UsageTracker? _usage;

    public MemoryBulkEndpoints(ToolDispatcher dispatcher, ExportService export, UsageTracker? usage)
    {
        _dispatcher = dispatcher;
        _export = export;
        _usage = usage;
    }

    public async Task Intake(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

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

    public async Task Reflect(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var dryRun = ctx.Request.QueryString["dryRun"];
        var source = ctx.Request.QueryString["source"];
        var args = JsonSerializer.SerializeToElement(new
        {
            dry_run = string.Equals(dryRun, "true", StringComparison.OrdinalIgnoreCase),
            source,
        }, HttpJson.Options);

        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_reflect", repo, args, "rest", ct));
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
}
