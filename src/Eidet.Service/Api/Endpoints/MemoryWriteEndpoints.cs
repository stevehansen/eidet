using System.Net;
using System.Text.Json;
using Eidet.Core.Services;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// Mutating memory routes: store, forget, feedback, content edit, link CRUD. Routes through
/// <see cref="ToolDispatcher"/> for parity with MCP; cross-repo link CRUD on a memory id calls
/// <see cref="MemoryService"/> directly.
/// </summary>
internal sealed class MemoryWriteEndpoints
{
    private readonly MemoryService _svc;
    private readonly ToolDispatcher _dispatcher;

    public MemoryWriteEndpoints(MemoryService svc, ToolDispatcher dispatcher)
    {
        _svc = svc;
        _dispatcher = dispatcher;
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
            reason = req.Reason,
        }, HttpJson.Options);

        var repo = ExtractRepoFromMemoryId(req.MemoryId) ?? "";
        var result = await _dispatcher.InvokeAsync(new ToolRequest("eidet_feedback", repo, args, "rest", ct));
        await RestFormatter.WriteAsync(ctx, result);
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

    /// <summary>
    /// Extract memory ID from paths like /api/eidet/{memoryId}/links. Memory IDs contain
    /// slashes (memories/repoSlug/type/hash), so we take everything between /api/eidet/ and /links.
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
        if (!memoryId.StartsWith("memories/", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = memoryId.Split('/');
        return parts.Length >= 3 ? parts[1].Replace("--", ":\\").Replace('-', '\\') : null;
    }
}
