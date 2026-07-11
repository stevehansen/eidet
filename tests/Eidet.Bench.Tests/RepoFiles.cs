namespace Eidet.Bench.Tests;

/// <summary>Repo-root file access for the sync guards (same idiom as <c>ScorecardSyncTests</c>).</summary>
internal static class RepoFiles
{
    public static bool WriteRequested =>
        Environment.GetEnvironmentVariable("EIDET_BENCH_WRITE") == "1";

    /// <summary>Walks up from the test's base directory until it finds the dir containing Eidet.slnx.</summary>
    public static string? FindRepoRoot()
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

    /// <summary>Compare on content, not line-ending flavor, so the guards are cross-platform.</summary>
    public static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");
}
