namespace Eidet.Bench.Tests;

/// <summary>
/// The anti-misreporting guard: no leaderboard-shaped number without the real dataset — in the
/// gate itself and in the rendered artifact's banner.
/// </summary>
public class LeaderboardGuardTests
{
    [Fact]
    public void FixtureDataset_MayNeverPublish_AndRefusalPointsAtTheRealDataset()
    {
        var fixture = new FixtureDataset();

        Assert.False(LeaderboardGuard.MayPublish(fixture));
        var refusal = LeaderboardGuard.Refusal(fixture);
        Assert.Contains("Refusing to emit a leaderboard number", refusal);
        Assert.Contains(LeaderboardGuard.DatasetUrl, refusal);
    }

    [Fact]
    public void RealButMissingDataset_IsRefused_WithDownloadHint()
    {
        var missing = new StubDataset(IsReal: true, Available: false);

        Assert.False(LeaderboardGuard.MayPublish(missing));
        var refusal = LeaderboardGuard.Refusal(missing, @"C:\data\swe");
        Assert.Contains("Refusing to emit a leaderboard number", refusal);
        Assert.Contains(@"C:\data\swe", refusal);
        Assert.Contains(LeaderboardGuard.DatasetUrl, refusal);
    }

    [Fact]
    public void RealAvailableDataset_MayPublish()
    {
        Assert.True(LeaderboardGuard.MayPublish(new StubDataset(IsReal: true, Available: true)));
    }

    [Fact]
    public async Task FixtureReport_CarriesTheNotALeaderboardBanner()
    {
        var (solver, oracle) = await FixtureScript.ScriptedPortsAsync();
        var report = await FixtureScript.NewHarness(new NoMemoryBackend(), solver, oracle).RunAsync(0);

        Assert.False(report.IsRealDataset);
        Assert.Contains("NOT a leaderboard number", report.ToMarkdown());
    }

    [Fact]
    public async Task RealRunReport_DropsTheFixtureBanner()
    {
        var (solver, oracle) = await FixtureScript.ScriptedPortsAsync();
        var fixtureReport = await FixtureScript.NewHarness(new NoMemoryBackend(), solver, oracle).RunAsync(0);

        var realReport = fixtureReport with { DatasetName = "SWEContextBench", IsRealDataset = true };
        var markdown = realReport.ToMarkdown();
        Assert.DoesNotContain("NOT a leaderboard number", markdown);
        Assert.Contains("Recorded real run", markdown);
    }

    private sealed record StubDataset(bool IsReal, bool Available) : ISweDatasetPort
    {
        public string Name => "stub";
        public bool IsRealDataset => IsReal;
        public bool IsAvailable => Available;
        public Task<IReadOnlyList<SweTask>> LoadAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SweTask>>([]);
    }
}
