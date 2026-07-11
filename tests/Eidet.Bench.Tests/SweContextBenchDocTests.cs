namespace Eidet.Bench.Tests;

/// <summary>
/// The deterministic harness logic guard from the issue #36 design: fixture dataset + replayed
/// solver/oracle + the in-process Eidet backend over the real <c>MemoryService</c>, byte-asserted
/// against the committed <c>docs/swe-context-bench.md</c> (the sibling artifact to
/// <c>docs/benchmark.md</c>). Recording happens in-test from the deterministic script; the run
/// that renders the document goes through <see cref="ReplaySolver"/>/<see cref="ReplayOracle"/>,
/// proving replay re-derives the identical numbers. Set <c>EIDET_BENCH_WRITE=1</c> to regenerate.
/// </summary>
public class SweContextBenchDocTests
{
    [Fact]
    public async Task CommittedReport_MatchesFixtureReplayRender()
    {
        var repoRoot = RepoFiles.FindRepoRoot();
        if (repoRoot is null) return; // not a source checkout — nothing to guard

        var transcript = await FixtureScript.RecordBothArmsAsync();
        var harness = FixtureScript.NewHarness(
            FixtureScript.NewEidetArm(), new ReplaySolver(transcript), new ReplayOracle(transcript));
        var rendered = (await harness.RunAsync(0)).ToMarkdown();

        var path = Path.Combine(repoRoot, "docs", "swe-context-bench.md");
        if (RepoFiles.WriteRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rendered);
            return;
        }

        Assert.True(File.Exists(path),
            "docs/swe-context-bench.md is missing. Regenerate it with EIDET_BENCH_WRITE=1.");

        var committed = File.ReadAllText(path);
        Assert.Equal(RepoFiles.Normalize(rendered), RepoFiles.Normalize(committed));
    }
}
