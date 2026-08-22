using System.Net;
using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoints for the maintenance pipeline. Routes through <see cref="ToolDispatcher"/>
/// for parity with MCP.
///
/// A run is synchronous while that is useful and asynchronous when it stops being useful: a small
/// repo finishes inside <see cref="GraceWindow"/> and answers 200 with the report, exactly as it
/// always did, while a long run answers 202 with a run id to poll. The alternative — blocking for
/// as long as it takes — made a large repo's successful run look like a failure to any client with
/// a timeout, because nothing distinguished "still working" from "dead".
/// </summary>
internal sealed class MaintenanceEndpoints
{
    /// <summary>
    /// How long a caller waits before being handed a run id instead of a report. Sized so the
    /// common case (a repo of a few hundred memories, no drift review) never sees a 202, and no
    /// caller waits long enough to hit its own timeout first.
    /// </summary>
    private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(30);

    private readonly ToolDispatcher _dispatcher;
    private readonly MaintenanceRuns _runs;

    public MaintenanceEndpoints(ToolDispatcher dispatcher, MaintenanceRuns runs)
    {
        _dispatcher = dispatcher;
        _runs = runs;
    }

    public async Task Maintenance(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var args = JsonDocument.Parse("{}").RootElement;
        var run = _runs.Start(
            RepoIdNormalizer.Normalize(repo),
            token => _dispatcher.InvokeAsync(new ToolRequest("eidet_maintenance", repo, args, "rest", token)),
            ct);

        if (await run.WaitAsync(GraceWindow, ct) is { } result)
        {
            await RestFormatter.WriteAsync(ctx, result);
            return;
        }

        await HttpJson.WriteAsync(ctx, MaintenanceRunEnvelope.Running(run), 202);
    }

    /// <summary>
    /// Reports on a run started earlier. The body is an envelope rather than a bare report, because
    /// a poller needs to know whether the report it is looking at is final.
    /// </summary>
    public async Task Run(HttpListenerContext ctx, string runId)
    {
        if (_runs.Find(runId) is not { } run)
        {
            await HttpJson.WriteAsync(ctx, new
            {
                error = $"Unknown maintenance run '{runId}' — results are kept for an hour after the run finishes",
            }, 404);
            return;
        }

        await HttpJson.WriteAsync(ctx, run.IsRunning
            ? MaintenanceRunEnvelope.Running(run)
            : MaintenanceRunEnvelope.Finished(run, await run.Work));
    }
}

/// <summary>
/// The wire shape of a run: what a 202 hands back and what polling returns. One type for both, so a
/// poller reading <c>status</c> and <c>report</c> is reading the same fields the 202 promised — three
/// SDKs parse this, and null fields are dropped on the way out (see <see cref="HttpJson.Options"/>),
/// so a running envelope carries no empty report to mistake for a finished one.
/// </summary>
internal sealed record MaintenanceRunEnvelope(
    string RunId,
    string Repo,
    string Status,
    DateTime StartedAt,
    string Poll,
    DateTime? CompletedAt = null,
    object? Report = null,
    string? Error = null)
{
    public static MaintenanceRunEnvelope Running(MaintenanceRun run) =>
        new(run.Id, run.RepoId, "running", run.StartedAt, RunPath(run.Id));

    public static MaintenanceRunEnvelope Finished(MaintenanceRun run, ToolResult result) =>
        new(run.Id, run.RepoId, result.IsOk ? "completed" : "failed", run.StartedAt, RunPath(run.Id),
            CompletedAt: run.CompletedAt,
            Report: result.IsOk ? result.Payload : null,
            Error: result.IsOk ? null : result.HumanSummary);

    public static string RunPath(string runId) => $"/api/maintenance/runs/{runId}";
}
