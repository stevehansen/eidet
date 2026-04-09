using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Domain;

public class RepoIdNormalizerTests
{
    [Theory]
    [InlineData(@"P:\TerminalHost", "P--TerminalHost")]
    [InlineData(@"P:\Eidet", "P--Eidet")]
    [InlineData("/home/user/projects/my-app", "-home-user-projects-my-app")]
    [InlineData(@"C:\Users\steve\repos\acme.utils", "C--Users-steve-repos-acme-utils")]
    [InlineData(@"P:\My_Project\", "P--My-Project")]
    public void Normalize_ConvertsPathToRepoId(string input, string expected)
    {
        Assert.Equal(expected, RepoIdNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_TrimsTrailingSlashes()
    {
        Assert.Equal("P--Eidet", RepoIdNormalizer.Normalize(@"P:\Eidet\"));
        Assert.Equal("P--Eidet", RepoIdNormalizer.Normalize(@"P:\Eidet/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_ReturnsUnknownForEmptyOrWhitespace(string? input)
    {
        Assert.Equal("unknown", RepoIdNormalizer.Normalize(input!));
    }
}
