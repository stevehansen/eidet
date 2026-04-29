using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;

namespace Eidet.Core.Tests.Intake.Extractors;

public class EditorConfigExtractorTests
{
    [Fact]
    public async Task EmitsSingleInsight_TaggedFormatting()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(".editorconfig", """
                                       # comment line that should be ignored
                                       root = true

                                       [*.cs]
                                       indent_style = space
                                       indent_size = 4
                                       """);
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();

        await new EditorConfigExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        var memory = Assert.Single(sink.Memories);
        Assert.Equal(".editorconfig", memory.Source);
        Assert.Contains("editorconfig", memory.Tags);
        Assert.Contains("formatting", memory.Tags);
        Assert.Contains("indent_size = 4", memory.Content);
        Assert.DoesNotContain("# comment", memory.Content);
    }
}
