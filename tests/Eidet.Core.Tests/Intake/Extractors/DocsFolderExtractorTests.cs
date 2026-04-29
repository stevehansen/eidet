using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;

namespace Eidet.Core.Tests.Intake.Extractors;

public class DocsFolderExtractorTests
{
    [Fact]
    public void DoesNotApply_WhenDocsPatternNotSet()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("docs/notes.md", "## Heading\nBody content here that is long enough.");
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };

        Assert.False(new DocsFolderExtractor().AppliesTo(ctx));
    }

    [Fact]
    public async Task RecursivelyEmitsMemories_TaggedByFilenameAndHeading()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("api-spec.md", "## RavenDB Setup\nDetailed configuration steps for the embedded database.");
        dir.WriteFile("sub/intake-flow.md", "## Pipeline\nExtractors run in registration order through the sink.");
        var ctx = new IntakeContext
        {
            RepoId = "test-repo",
            ProjectPath = dir.Path,
            Options = new IntakeOptions
            {
                DocsPattern = "*.md",
                DocsRecursive = true,
                DocsImportance = 0.5f,
                DocsExtraTags = ["docs"],
            },
        };
        var sink = new FakeIntakeSink();

        Assert.True(new DocsFolderExtractor().AppliesTo(ctx));
        await new DocsFolderExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Equal(2, sink.Memories.Count);

        var apiMem = Assert.Single(sink.Memories, m => m.Source == "api-spec.md");
        Assert.Equal(0.5f, apiMem.Importance);
        Assert.Contains("api", apiMem.Tags);
        Assert.Contains("ravendb", apiMem.Tags);
        Assert.Contains("docs", apiMem.Tags);
    }
}
