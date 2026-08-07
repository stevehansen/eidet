using Eidet.Core.Domain;
using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;

namespace Eidet.Core.Tests.Intake.Extractors;

public class AgentsMdExtractorTests
{
    [Fact]
    public void DoesNotApply_WhenNoAgentsMd()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("README.md", "## Intro\nSome readme content long enough to matter.");
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };

        Assert.False(new AgentsMdExtractor().AppliesTo(ctx));
    }

    [Fact]
    public async Task EmitsInsights_FromRootAndNestedFiles()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("AGENTS.md", "## Build Rules\nAlways run the full test suite before committing.");
        dir.WriteFile("src/api/AGENTS.md", "## Endpoint Conventions\nEvery route goes through the dispatcher for parity.");
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();
        var extractor = new AgentsMdExtractor();

        Assert.True(extractor.AppliesTo(ctx));
        await extractor.ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Equal(2, sink.Memories.Count);
        Assert.All(sink.Memories, m =>
        {
            Assert.Equal(MemoryType.Insight, m.Type);
            Assert.Equal(0.5f, m.Importance);
            Assert.Contains("agents", m.Tags);
        });

        var root = Assert.Single(sink.Memories, m => m.Source == "AGENTS.md");
        Assert.Contains("build", root.Tags);
        Assert.Contains("Always run the full test suite", root.Content);

        var nested = Assert.Single(sink.Memories, m => m.Source == Path.Combine("src", "api", "AGENTS.md"));
        Assert.Contains("endpoint", nested.Tags);
    }

    [Fact]
    public async Task SkipsNoiseDirectories()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("AGENTS.md", "## Root\nRoot-level agent instructions with enough length.");
        dir.WriteFile("node_modules/pkg/AGENTS.md", "## Vendored\nThird-party agent instructions that must not be ingested.");
        dir.WriteFile(".git/AGENTS.md", "## GitInternals\nNever read from the git dir either, obviously.");
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();

        await new AgentsMdExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        var memory = Assert.Single(sink.Memories);
        Assert.Equal("AGENTS.md", memory.Source);
    }
}
