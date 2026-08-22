using Eidet.Service.Api;
using Eidet.Service.Api.Endpoints;
using Eidet.Service.Tools;

namespace Eidet.Service.Tests.Api;

/// <summary>
/// The authority on a run outliving its request: the grace window decides who waits, never whether
/// the work continues, and a run that has stopped being waited on still reaches its report.
/// </summary>
public class MaintenanceRunsTests
{
    private static readonly TimeSpan NoGrace = TimeSpan.Zero;
    private static readonly TimeSpan LongGrace = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WorkFinishingInsideTheGraceWindow_IsReturnedToTheWaiter()
    {
        var runs = new MaintenanceRuns();

        var run = runs.Start("P--Repo", _ => Task.FromResult(ToolResult.Ok(payload: new { done = true }, "ok")), default);
        var result = await run.WaitAsync(LongGrace, default);

        Assert.NotNull(result);
        Assert.Equal(ToolStatus.Ok, result.Status);
    }

    [Fact]
    public async Task WorkOutlivingTheGraceWindow_KeepsGoingAndStaysPollable()
    {
        var runs = new MaintenanceRuns();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = runs.Start("P--Repo", async _ =>
        {
            await gate.Task;
            return ToolResult.Ok(payload: new { done = true }, "ok");
        }, default);

        Assert.Null(await run.WaitAsync(NoGrace, default));
        Assert.True(run.IsRunning);
        Assert.Same(run, runs.Find(run.Id));

        gate.SetResult();
        var result = await runs.Find(run.Id)!.Work;

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.False(run.IsRunning);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public async Task WorkThatThrows_BecomesAResultRatherThanAFaultedTask()
    {
        var runs = new MaintenanceRuns();

        var run = runs.Start("P--Repo", _ => throw new InvalidOperationException("boom"), default);
        var result = await run.WaitAsync(LongGrace, default);

        Assert.NotNull(result);
        Assert.Equal(ToolStatus.Internal, result.Status);
        Assert.Contains("boom", result.HumanSummary);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public void UnknownRunId_IsNotFound()
    {
        Assert.Null(new MaintenanceRuns().Find("nosuchrun"));
    }

    /// <summary>
    /// The three SDKs and the Web UI all branch on <c>status</c> and read <c>report</c>, so these
    /// names and the drop-nulls rule are the wire contract, not an implementation detail.
    /// </summary>
    [Fact]
    public async Task TheEnvelope_IsTheContractThreeClientsParse()
    {
        var runs = new MaintenanceRuns();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = runs.Start("P--Repo", async _ =>
        {
            await gate.Task;
            return ToolResult.Ok(payload: new { repoId = "P--Repo" }, "ok");
        }, default);

        var running = Serialize(MaintenanceRunEnvelope.Running(run));

        Assert.Contains($"\"runId\":\"{run.Id}\"", running);
        Assert.Contains("\"status\":\"running\"", running);
        Assert.Contains($"\"poll\":\"/api/maintenance/runs/{run.Id}\"", running);
        Assert.DoesNotContain("report", running);
        Assert.DoesNotContain("completedAt", running);

        gate.SetResult();
        var finished = Serialize(MaintenanceRunEnvelope.Finished(run, await run.Work));

        Assert.Contains("\"status\":\"completed\"", finished);
        Assert.Contains("\"report\":{\"repoId\":\"P--Repo\"}", finished);
        Assert.Contains("\"completedAt\"", finished);
        Assert.DoesNotContain("error", finished);
    }

    [Fact]
    public async Task AFailedRun_ReportsTheErrorInsteadOfAnEmptyReport()
    {
        var runs = new MaintenanceRuns();

        var run = runs.Start("P--Repo", _ => throw new InvalidOperationException("boom"), default);
        var envelope = Serialize(MaintenanceRunEnvelope.Finished(run, await run.Work));

        Assert.Contains("\"status\":\"failed\"", envelope);
        Assert.Contains("boom", envelope);
        Assert.DoesNotContain("report", envelope);
    }

    private static string Serialize(MaintenanceRunEnvelope envelope) =>
        System.Text.Json.JsonSerializer.Serialize(envelope, HttpJson.Options);

    /// <summary>
    /// Two POSTs are two runs. Deduplicating them is the runner's job, not this table's, so it must
    /// not quietly hand the second caller the first caller's handle.
    /// </summary>
    [Fact]
    public void TwoRunsForOneRepo_GetDistinctIds()
    {
        var runs = new MaintenanceRuns();
        var work = (CancellationToken _) => Task.FromResult(ToolResult.Ok(payload: new { }, "ok"));

        var first = runs.Start("P--Repo", work, default);
        var second = runs.Start("P--Repo", work, default);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Same(first, runs.Find(first.Id));
        Assert.Same(second, runs.Find(second.Id));
    }
}
