namespace Eidet.Bench.Tests;

/// <summary>
/// Guards that the committed <c>tools/Eidet.Bench/Fixtures/fixture-transcript.json</c> (embedded
/// into the assembly for the `eidet bench` smoke path) is exactly what recording both fixture
/// arms produces. Set <c>EIDET_BENCH_WRITE=1</c> to regenerate instead of asserting. Skips
/// silently off a source checkout, like <c>ScorecardSyncTests</c>.
/// </summary>
public class FixtureTranscriptSyncTests
{
    [Fact]
    public async Task CommittedTranscript_MatchesRecordedFixtureRun()
    {
        var repoRoot = RepoFiles.FindRepoRoot();
        if (repoRoot is null) return; // not a source checkout — nothing to guard

        var path = Path.Combine(repoRoot, "tools", "Eidet.Bench", "Fixtures", "fixture-transcript.json");
        var recorded = (await FixtureScript.RecordBothArmsAsync()).ToJson();

        if (RepoFiles.WriteRequested)
        {
            File.WriteAllText(path, recorded);
            return;
        }

        Assert.True(File.Exists(path),
            "fixture-transcript.json is missing. Regenerate it with EIDET_BENCH_WRITE=1 (then rebuild to re-embed).");

        var committed = File.ReadAllText(path);
        Assert.Equal(RepoFiles.Normalize(recorded), RepoFiles.Normalize(committed));
    }
}
