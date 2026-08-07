using Eidet.Core.Domain;
using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;

namespace Eidet.Core.Tests.Intake.Extractors;

public class ClaudeMdExtractorTests
{
    [Fact]
    public async Task EmitsOneInsightPerSection_FromClaudeMd()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("CLAUDE.md", """
                                   ## Architecture
                                   Layered service with REST and MCP transports going through the dispatcher.

                                   ## Testing
                                   Run dotnet test against the embedded RavenDB store from CI.
                                   """);
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();
        var extractor = new ClaudeMdExtractor();

        Assert.True(extractor.AppliesTo(ctx));
        await extractor.ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Equal(2, sink.Memories.Count);
        Assert.All(sink.Memories, m =>
        {
            Assert.Equal(MemoryType.Insight, m.Type);
            Assert.Equal("CLAUDE.md", m.Source);
            Assert.Equal(0.5f, m.Importance);
        });
    }

    [Fact]
    public async Task PicksUpLegacyMemoryMd()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("MEMORY.md", "## Notes\nLegacy memory file format from earlier Claude Code versions.");
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();

        await new ClaudeMdExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Single(sink.Memories);
        Assert.Equal("MEMORY.md", sink.Memories[0].Source);
    }

    [Fact]
    public void DoesNotApply_WhenNoFilesPresent()
    {
        using var dir = new TempDirectory();
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };

        Assert.False(new ClaudeMdExtractor().AppliesTo(ctx));
    }
}
