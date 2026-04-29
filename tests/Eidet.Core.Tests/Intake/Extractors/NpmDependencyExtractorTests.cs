using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;

namespace Eidet.Core.Tests.Intake.Extractors;

public class NpmDependencyExtractorTests
{
    [Fact]
    public async Task EmitsLinks_FromDependenciesAndDevDependencies()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("package.json", """
                                      {
                                        "name": "demo",
                                        "dependencies": { "react": "18.0.0", "lodash": "4.17.21" },
                                        "devDependencies": { "jest": "29.0.0" }
                                      }
                                      """);
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();

        await new NpmDependencyExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Equal(3, sink.Links.Count);
        Assert.Contains(sink.Links, l => l.TargetRepoId == "npm:react");
        Assert.Contains(sink.Links, l => l.TargetRepoId == "npm:lodash");
        Assert.Contains(sink.Links, l => l.TargetRepoId == "npm:jest");
    }

    [Fact]
    public async Task MalformedJson_RecordsSkipped()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("package.json", "{ this is not valid JSON ");
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();

        await new NpmDependencyExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Empty(sink.Links);
        Assert.Single(sink.Skipped, t => t.Source == "package.json" && t.Reason == "malformed JSON");
    }

    [Fact]
    public void DoesNotApply_WithoutPackageJson()
    {
        using var dir = new TempDirectory();
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };

        Assert.False(new NpmDependencyExtractor().AppliesTo(ctx));
    }
}
