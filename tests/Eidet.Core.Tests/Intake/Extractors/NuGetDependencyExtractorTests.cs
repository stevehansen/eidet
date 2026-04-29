using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;

namespace Eidet.Core.Tests.Intake.Extractors;

public class NuGetDependencyExtractorTests
{
    [Fact]
    public async Task EmitsLinks_ForPackageReferences()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("Test.csproj", """
                                     <Project Sdk="Microsoft.NET.Sdk">
                                       <ItemGroup>
                                         <PackageReference Include="Spectre.Console" Version="0.55.0" />
                                         <PackageReference Include="RavenDB.Client" Version="7.0.0" />
                                       </ItemGroup>
                                     </Project>
                                     """);
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();

        await new NuGetDependencyExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Equal(2, sink.Links.Count);
        Assert.Contains(sink.Links, l => l.TargetRepoId == "nuget:Spectre.Console");
        Assert.Contains(sink.Links, l => l.TargetRepoId == "nuget:RavenDB.Client");
        Assert.All(sink.Links, l => Assert.Equal("depends-on", l.Relation));
    }

    [Fact]
    public async Task EmitsProducedPackage_FromPackageId()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("Test.csproj", """
                                     <Project Sdk="Microsoft.NET.Sdk">
                                       <PropertyGroup>
                                         <PackageId>Eidet.Sdk</PackageId>
                                       </PropertyGroup>
                                     </Project>
                                     """);
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();

        await new NuGetDependencyExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Single(sink.ProducedPackages, "Eidet.Sdk");
    }

    [Fact]
    public async Task RecursesIntoSubDirectories()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/A/A.csproj", """<Project><ItemGroup><PackageReference Include="A.Pkg"/></ItemGroup></Project>""");
        dir.WriteFile("src/B/B.csproj", """<Project><ItemGroup><PackageReference Include="B.Pkg"/></ItemGroup></Project>""");
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };
        var sink = new FakeIntakeSink();

        await new NuGetDependencyExtractor().ExtractAsync(ctx, sink, CancellationToken.None);

        Assert.Equal(2, sink.Links.Count);
    }

    [Fact]
    public void DoesNotApply_WithoutCsproj()
    {
        using var dir = new TempDirectory();
        var ctx = new IntakeContext { RepoId = "test-repo", ProjectPath = dir.Path };

        Assert.False(new NuGetDependencyExtractor().AppliesTo(ctx));
    }
}
