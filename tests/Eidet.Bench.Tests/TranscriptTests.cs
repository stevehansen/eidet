namespace Eidet.Bench.Tests;

/// <summary>
/// Record/replay is the reproducibility mechanism for the paid run: recording must round-trip
/// through JSON to identical replayed results, replay must fail loudly on anything unrecorded,
/// and re-recording must reject drift.
/// </summary>
public class TranscriptTests
{
    private static readonly SolveRequest Request = new(
        "acme__parquet-tools-101", "acme/parquet-tools", "3f1c2a9d", "the problem", ["a fragment"]);

    private static readonly SweTask Task201 = new(
        "acme/parquet-tools", "acme__parquet-tools-201", "a1b2c3", "patch", "test-patch",
        "problem", "", "2026-01-08T11:05:00Z", 1.3, "[]", "[]", "a1b2c3", IsRelated: false);

    [Fact]
    public async Task RecordedRun_RoundTripsThroughJson_ToIdenticalReplay()
    {
        var recorded = await FixtureScript.RecordBothArmsAsync();
        var replayed = Transcript.FromJson(recorded.ToJson());

        var (solver, oracle) = await FixtureScript.ScriptedPortsAsync();
        var direct = await FixtureScript.NewHarness(FixtureScript.NewEidetArm(), solver, oracle).RunAsync(0);
        var viaReplay = await FixtureScript
            .NewHarness(FixtureScript.NewEidetArm(), new ReplaySolver(replayed), new ReplayOracle(replayed))
            .RunAsync(0);

        Assert.Equal(direct.Resolved, viaReplay.Resolved);
        Assert.Equal(direct.SolveTokens, viaReplay.SolveTokens);
        Assert.Equal(direct.ToMarkdown(), viaReplay.ToMarkdown());
    }

    [Fact]
    public void ToJson_IsStable_AcrossSerializeParseSerialize()
    {
        var transcript = new Transcript();
        transcript.RecordSolve(Request, new SolveResult("a patch", 42));
        transcript.RecordVerdict(Task201, "a patch", new Verdict(true, true));

        var json = transcript.ToJson();
        Assert.Equal(json, Transcript.FromJson(json).ToJson());
        Assert.Contains(Transcript.Format, json);
    }

    [Fact]
    public async Task ReplaySolver_UnrecordedRequest_ThrowsInsteadOfFabricating()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReplaySolver(new Transcript()).AttemptAsync(Request));
        Assert.Contains("re-record", ex.Message);
    }

    [Fact]
    public async Task ReplayOracle_UnrecordedVerdict_ThrowsInsteadOfFabricating()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReplayOracle(new Transcript()).ResolveAsync(Task201, "unseen patch"));
        Assert.Contains("re-record", ex.Message);
    }

    [Fact]
    public void RecordSolve_SameRequestDifferentResult_ThrowsDriftGuard()
    {
        var transcript = new Transcript();
        transcript.RecordSolve(Request, new SolveResult("patch-a", 10));
        transcript.RecordSolve(Request, new SolveResult("patch-a", 10)); // identical re-record is fine

        Assert.Throws<InvalidOperationException>(
            () => transcript.RecordSolve(Request, new SolveResult("patch-b", 10)));
    }

    [Fact]
    public void KeyForSolve_DependsOnContext()
    {
        // The recalled context changes what the solver sees, so it must change the cache key.
        var withOtherContext = Request with { Context = ["another fragment"] };
        Assert.NotEqual(Transcript.KeyForSolve(Request), Transcript.KeyForSolve(withOtherContext));
        Assert.Equal(Transcript.KeyForSolve(Request), Transcript.KeyForSolve(Request with { }));
    }
}
