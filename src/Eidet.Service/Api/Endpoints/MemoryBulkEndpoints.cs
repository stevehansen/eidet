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
        var path = await ResolveRepoPathAsync(ctx);
        if (path is null) return;

        var args = JsonSerializer.SerializeToElement(new { }, HttpJson.Options);
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_intake", path, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task IntakeGit(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = await ResolveRepoPathAsync(ctx);
        if (path is null) return;

        var q = ctx.Request.QueryString;
        var args = JsonSerializer.SerializeToElement(new
        {
            dry_run = string.Equals(q["dry_run"], "true", StringComparison.OrdinalIgnoreCase),
            since = q["since"],
            max_commits = int.TryParse(q["max_commits"], out var maxCommits) ? maxCommits : 500,
            all_commits = string.Equals(q["all_commits"], "true", StringComparison.OrdinalIgnoreCase),
        }, HttpJson.Options);

        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_intake_git", path, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    public async Task IntakeClaudeMemory(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = await ResolveRepoPathAsync(ctx);
        if (path is null) return;

        var args = JsonSerializer.SerializeToElement(new
        {
            dry_run = string.Equals(ctx.Request.QueryString["dry_run"], "true", StringComparison.OrdinalIgnoreCase),
        }, HttpJson.Options);

        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_intake_claude_memory", path, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
    }

    /// <summary>
    /// Resolves the `repo` query param to an existing filesystem path (explicit `path` param,
    /// path-shaped repo id, or the tracked original path). Writes the 400 response and returns
    /// null when it can't.
    /// </summary>
    private async Task<string?> ResolveRepoPathAsync(HttpListenerContext ctx)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return null;
        }

        var path = ctx.Request.QueryString["path"];
        if (string.IsNullOrEmpty(path))
            path = RepoUsage.LooksLikePath(repo) ? repo : null;
        if (string.IsNullOrEmpty(path) && _usage is not null)
            path = await _usage.GetOriginalPathAsync(repo);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            await HttpJson.WriteAsync(ctx, new { error = $"Cannot resolve filesystem path for repo '{repo}'. The path '{path ?? "(unknown)"}' does not exist." }, 400);
            return null;
        }
        return path;
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
        var normalized = RepoIdNormalizer.Normalize(repo);
        // format=agents renders the AGENTS.md interop shape; default stays the memory dump.
        var markdown = string.Equals(ctx.Request.QueryString["format"], "agents", StringComparison.OrdinalIgnoreCase)
            ? await _export.ExportAgentsMdAsync(normalized, ct)
            : await _export.ExportMarkdownAsync(normalized, ct);
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
