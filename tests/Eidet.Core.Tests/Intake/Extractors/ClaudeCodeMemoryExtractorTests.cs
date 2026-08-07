using Eidet.Core.Domain;
using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;

namespace Eidet.Core.Tests.Intake.Extractors;

/// <summary>
/// Exercises the external-home-dir extractor through the injectable base-dir seam — the
/// real <c>~/.claude</c> is never touched.
/// </summary>
public class ClaudeCodeMemoryExtractorTests
{
    private static IntakeContext Ctx(string projectPath, bool claudeMemory = true) => new()
    {
        RepoId = "test-repo",
        ProjectPath = projectPath,
        Options = new IntakeOptions { ClaudeMemory = claudeMemory },
    };

    private static string StageMemoryDir(TempDirectory home, string projectPath, params (string Name, string Content)[] files)
    {
        var slug = RepoIdNormalizer.Normalize(projectPath);
        var dir = Path.Combine("projects", slug, "memory");
        foreach (var (name, content) in files)
            home.WriteFile(Path.Combine(dir, name), content);
        return Path.Combine(home.Path, dir);
    }

    [Fact]
    public void DoesNotApply_WithoutOptIn_EvenWhenMemoryExists()
    {
        using var home = new TempDirectory();
        StageMemoryDir(home, "/proj/x", ("MEMORY.md", "## Notes\nSome memory content long enough."));

        var extractor = new ClaudeCodeMemoryExtractor(home.Path);

        Assert.False(extractor.AppliesTo(Ctx("/proj/x", claudeMemory: false)));
    }

    [Fact]
    public void DoesNotApply_WhenNoMemoryDirForProject()
    {
        using var home = new TempDirectory();

        Assert.False(new ClaudeCodeMemoryExtractor(home.Path).AppliesTo(Ctx("/proj/x")));
    }

    [Fact]
    public async Task EmitsInsights_FromProjectMemoryFiles_Only()
    {
        using var home = new TempDirectory();
        StageMemoryDir(home, "/proj/x",
            ("MEMORY.md", "## Deployment\nThe service restarts via the scheduler after updates."),
            ("testing-notes.md", "## Flaky Suite\nIntegration tests need the embedded RavenDB warm-up."));
        // Another project's memory must never be read.
        StageMemoryDir(home, "/proj/OTHER", ("MEMORY.md", "## Foreign\nBelongs to a different project entirely."));

        var ctx = Ctx("/proj/x");
        var sink = new FakeIntakeSink();
        var extractor = new ClaudeCodeMemoryExtractor(home.Path);

        Assert.True(extractor.AppliesTo(ctx));
        await extractor.ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Equal(2, sink.Memories.Count);
        Assert.All(sink.Memories, m =>
        {
            Assert.Equal(MemoryType.Insight, m.Type);
            Assert.Equal(0.5f, m.Importance);
            Assert.Contains("claude-code", m.Tags);
            Assert.StartsWith("claude-memory/", m.Source);
        });
        Assert.DoesNotContain(sink.Memories, m => m.Content.Contains("Foreign"));

        var notes = Assert.Single(sink.Memories, m => m.Source == "claude-memory/testing-notes.md");
        Assert.Contains("testing", notes.Tags);
        Assert.Contains("flaky", notes.Tags);
    }
}
