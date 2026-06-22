using Eidet.Core.Benchmark;

namespace Eidet.Benchmark.Tests;

/// <summary>
/// Guards that the committed <c>docs/benchmark.md</c> is the truthful, current output of
/// <see cref="BenchmarkReport.ToMarkdown"/>. Set <c>EIDET_BENCH_WRITE=1</c> to regenerate the file
/// instead of asserting (the dev escape hatch). Skips silently when the repo root can't be located
/// (e.g. a packaged run), so the guard never produces a false failure off a developer checkout.
/// </summary>
public class ScorecardSyncTests
{
    [Fact]
    public async Task CommittedScorecard_MatchesRenderedMarkdown()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return; // not a source checkout — nothing to guard

        var path = Path.Combine(repoRoot, "docs", "benchmark.md");
        var report = await ScorecardBuilder.BuildAsync();
        var rendered = report.ToMarkdown();

        if (Environment.GetEnvironmentVariable("EIDET_BENCH_WRITE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rendered);
            return;
        }

        Assert.True(File.Exists(path),
            $"docs/benchmark.md is missing. Regenerate it with EIDET_BENCH_WRITE=1.");

        var committed = File.ReadAllText(path);
        Assert.Equal(Normalize(rendered), Normalize(committed));
    }

    /// <summary>Walks up from the test's base directory until it finds the dir containing Eidet.slnx.</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Eidet.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Compare on content, not line-ending flavor, so the guard is cross-platform.</summary>
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");
}
