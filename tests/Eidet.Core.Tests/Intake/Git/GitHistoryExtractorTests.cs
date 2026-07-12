using Eidet.Core.Domain;
using Eidet.Core.Intake;
using Eidet.Core.Intake.Git;

namespace Eidet.Core.Tests.Intake.Git;

/// <summary>
/// Gate → mine behavior of <see cref="GitHistoryExtractor"/> over in-memory fixtures —
/// zero subprocess, zero real repo.
/// </summary>
public class GitHistoryExtractorTests
{
    private static IntakeContext Ctx(GitIntakeOptions? git = null) => new()
    {
        RepoId = "test-repo",
        ProjectPath = "/x",
        Options = new IntakeOptions { Git = git ?? new GitIntakeOptions() },
    };

    private static async Task<FakeIntakeSink> RunAsync(InMemoryGitHistorySource git, GitIntakeOptions? options = null)
    {
        var sink = new FakeIntakeSink();
        await new GitHistoryExtractor(git).ExtractAsync(Ctx(options), sink, CancellationToken.None);
        return sink;
    }

    [Fact]
    public void DoesNotApply_WhenGitOptionsNotSet()
    {
        var extractor = new GitHistoryExtractor(new InMemoryGitHistorySource());
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = "/x" };

        Assert.False(extractor.AppliesTo(ctx));
    }

    [Fact]
    public void DoesNotApply_WhenSourceUnavailable()
    {
        var extractor = new GitHistoryExtractor(new InMemoryGitHistorySource { IsAvailable = false });

        Assert.False(extractor.AppliesTo(Ctx()));
    }

    [Fact]
    public async Task FixCommit_MinesProcedure_WithPatternNotRawDiff()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("a1b2c3", "fix: null deref in RecallScorer when tags empty",
                files: ["src/RecallScorer.cs"], hunk: "- if(tags.Count>0)\n+ if(tags is {Count:>0})");

        var sink = await RunAsync(git);

        var memory = Assert.Single(sink.Memories);
        Assert.Equal(MemoryType.Procedure, memory.Type);
        Assert.StartsWith("fix: null deref in RecallScorer when tags empty", memory.Content);
        Assert.Contains("Fix pattern: src/RecallScorer.cs (+1/-1)", memory.Content);
        Assert.EndsWith("commit:a1b2c3", memory.Content);
        // Raw hunk lines must never be stored — only the derived pattern.
        Assert.DoesNotContain("if(tags is {Count:>0})", memory.Content);

        Assert.Contains("git-intake", memory.Tags);
        Assert.Contains("commit:a1b2c3", memory.Tags);
        Assert.Contains("fix", memory.Tags);
        Assert.Contains("recallscorer", memory.Tags);
    }

    [Fact]
    public async Task HunkHeaderContext_MinedAsRegions()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("b2c3d4", "fix: trust floor applied twice", files: ["src/MemoryTrust.cs"])
            .AddDiff("b2c3d4", "src/MemoryTrust.cs",
                "@@ -36,7 +36,9 @@ public static double Factor(MemoryEntry entry)",
                "-        var lift = 0;", "+        var lift = 1;");

        var sink = await RunAsync(git);

        var memory = Assert.Single(sink.Memories);
        Assert.Contains("Regions: public static double Factor(MemoryEntry entry)", memory.Content);
        Assert.DoesNotContain("var lift", memory.Content);
    }

    [Theory]
    [InlineData("feat(recall): add graph expansion", "recall")]
    [InlineData("refactor: extract maintenance stages", null)]
    [InlineData("perf: cache context slices", null)]
    public async Task ConventionalNonFix_MinesInsight(string subject, string? scopeTag)
    {
        var git = new InMemoryGitHistorySource().AddCommit("c3d4e5", subject, files: ["src/A.cs"]);

        var sink = await RunAsync(git);

        var memory = Assert.Single(sink.Memories);
        Assert.Equal(MemoryType.Insight, memory.Type);
        Assert.Contains("Change pattern: src/A.cs (+1/-1)", memory.Content);
        if (scopeTag is not null) Assert.Contains(scopeTag, memory.Tags);
    }

    [Theory]
    [InlineData("chore: bump dependencies", "commit type 'chore' not mined")]
    [InlineData("docs: fix typos", "commit type 'docs' not mined")]
    [InlineData("Update stuff in the scorer", "non-conventional commit message")]
    public async Task NoiseCommit_SkippedWithReason(string subject, string expectedReason)
    {
        var git = new InMemoryGitHistorySource().AddCommit("d4e5f6", subject, files: ["src/A.cs"]);

        var sink = await RunAsync(git);

        Assert.Empty(sink.Memories);
        var (source, reason) = Assert.Single(sink.Skipped);
        Assert.Equal("commit d4e5f6", source);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public async Task IncludeNonConventional_WidensGate_FixWordMapsToProcedure()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("e5f6a7", "Fixed the crash when parsing empty numstat blocks", files: ["src/P.cs"])
            .AddCommit("f6a7b8", "Introduce layered recall budgets for tiers", files: ["src/Q.cs"]);

        var sink = await RunAsync(git, new GitIntakeOptions(IncludeNonConventional: true));

        Assert.Equal(2, sink.Memories.Count);
        Assert.Equal(MemoryType.Procedure, Assert.Single(sink.Memories, m => m.Source == "commit e5f6a7").Type);
        Assert.Equal(MemoryType.Insight, Assert.Single(sink.Memories, m => m.Source == "commit f6a7b8").Type);
    }

    [Fact]
    public async Task MergeCommit_WithPrSubject_MinesInsight()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("aa11bb", "Merge pull request #61 from renovate/typescript", isMerge: true);

        var sink = await RunAsync(git);

        var memory = Assert.Single(sink.Memories);
        Assert.Equal(MemoryType.Insight, memory.Type);
    }

    [Fact]
    public async Task MergeCommit_WithoutDescription_Skipped()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("bb22cc", "Merge branch 'main' into feature", isMerge: true);

        var sink = await RunAsync(git);

        Assert.Empty(sink.Memories);
        Assert.Equal("merge commit without description", Assert.Single(sink.Skipped).Reason);
    }

    [Fact]
    public async Task BulkCommit_Skipped()
    {
        var files = Enumerable.Range(0, 26).Select(i => $"src/File{i}.cs").ToList();
        var git = new InMemoryGitHistorySource().AddCommit("cc33dd", "fix: mass rename", files: files);

        var sink = await RunAsync(git);

        Assert.Empty(sink.Memories);
        Assert.Contains("bulk change", Assert.Single(sink.Skipped).Reason);
    }

    [Fact]
    public async Task Since_BoundsHistory_Exclusively()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("s0", "fix: old already-processed commit", files: ["src/Old.cs"])
            .AddCommit("s1", "fix: new commit after the watermark", files: ["src/New.cs"]);

        var sink = await RunAsync(git, new GitIntakeOptions(Since: "s0"));

        var memory = Assert.Single(sink.Memories);
        Assert.Equal("commit s1", memory.Source);
    }

    [Fact]
    public async Task MaxCommits_BoundsHistory_NewestFirst()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("m0", "fix: oldest", files: ["src/A.cs"])
            .AddCommit("m1", "fix: middle", files: ["src/B.cs"])
            .AddCommit("m2", "fix: newest", files: ["src/C.cs"]);

        var sink = await RunAsync(git, new GitIntakeOptions(MaxCommits: 1));

        var memory = Assert.Single(sink.Memories);
        Assert.Equal("commit m2", memory.Source);
        Assert.Empty(sink.Skipped);
    }
}
