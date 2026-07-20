using System.Net;
using Eidet.Core.Canon;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// Canon (curated knowledge base) REST routes — the off-MCP Operator surface. Every route calls
/// <see cref="CanonService"/> DIRECTLY (no <c>ToolDispatcher</c>): Canon has no MCP tool by design, so
/// there is no parity path to preserve. Draft ids are slash-bearing
/// (<c>canondrafts/{repo}/{kind}/{slug}</c>), so approve/reject carry the id as the path between the
/// route prefix and the verb suffix, and GET carries it as the whole suffix after the drafts prefix.
/// </summary>
internal sealed class CanonEndpoints
{
    private const string DraftsPrefix = "/api/eidet/canon/drafts/";

    private readonly CanonService _canon;

    public CanonEndpoints(CanonService canon)
    {
        _canon = canon;
    }

    public async Task ListDrafts(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var max = int.TryParse(ctx.Request.QueryString["limit"], out var parsed)
            ? Math.Clamp(parsed, 1, 200)
            : 50;
        var drafts = await _canon.ListPendingAsync(repo, max, ct);
        await HttpJson.WriteAsync(ctx, new { repo, drafts });
    }

    public async Task GetDraft(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid draft id in path" }, 400);
            return;
        }

        var detail = await _canon.GetDraftAsync(Uri.UnescapeDataString(id), ct);
        if (detail is null)
        {
            await HttpJson.WriteAsync(ctx, new { error = "Draft not found" }, 404);
            return;
        }
        await HttpJson.WriteAsync(ctx, detail);
    }

    public async Task Approve(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid draft id in path" }, 400);
            return;
        }

        var req = await HttpJson.ReadAsync<ApproveRequest>(ctx); // body optional
        var r = await _canon.ApproveAsync(Uri.UnescapeDataString(id), req?.EditedContent, ct);
        await HttpJson.WriteAsync(ctx, r, StatusFor(r.Success, r.Reason));
    }

    public async Task Reject(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid draft id in path" }, 400);
            return;
        }

        var req = await HttpJson.ReadAsync<RejectRequest>(ctx);
        if (req is null || string.IsNullOrWhiteSpace(req.Reason))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required field: reason" }, 400);
            return;
        }

        var r = await _canon.RejectAsync(Uri.UnescapeDataString(id), req.Reason, ct);
        await HttpJson.WriteAsync(ctx, r, StatusFor(r.Success, r.Reason));
    }

    public async Task Regenerate(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"] ?? (await HttpJson.ReadAsync<RepoRequest>(ctx))?.Repo;
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var count = await _canon.RegenerateDraftsAsync(repo, ct);
        await HttpJson.WriteAsync(ctx, new { repo, drafts = count });
    }

    public async Task BulkApprove(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await HttpJson.ReadAsync<BulkApproveRequest>(ctx);
        var repo = req?.Repo ?? ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(req?.Source))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required fields: repo, source" }, 400);
            return;
        }

        var r = await _canon.BulkApproveAsync(repo, req.Source, ct);
        await HttpJson.WriteAsync(ctx, r);
    }

    /// <summary>Draft id for <c>GET /api/eidet/canon/drafts/{id}</c> — everything after the drafts prefix
    /// (the id itself contains slashes: <c>canondrafts/{repo}/{kind}/{slug}</c>).</summary>
    public static string ExtractIdFromGetPath(string path) =>
        path.StartsWith(DraftsPrefix) ? path[DraftsPrefix.Length..] : "";

    /// <summary>Draft id for <c>POST /api/eidet/canon/drafts/{id}/approve|reject</c> — the path between the
    /// drafts prefix and the verb suffix.</summary>
    public static string ExtractIdFromVerbPath(string path, string suffix)
    {
        if (path.StartsWith(DraftsPrefix) && path.EndsWith(suffix) && path.Length > DraftsPrefix.Length + suffix.Length)
            return path[DraftsPrefix.Length..^suffix.Length];
        return "";
    }

    // A claim we lost (or any non-not-found rejection) is a 409 conflict, unknown draft is 404.
    private static int StatusFor(bool success, string? reason) =>
        success ? 200 : reason == "not found" ? 404 : 409;

    private sealed record ApproveRequest(string? EditedContent);
    private sealed record RejectRequest(string? Reason);
    private sealed record RepoRequest(string? Repo);
    private sealed record BulkApproveRequest(string? Repo, string? Source);
}
