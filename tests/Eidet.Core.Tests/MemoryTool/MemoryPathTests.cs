using Eidet.Core.MemoryTool;

namespace Eidet.Core.Tests.MemoryTool;

public class MemoryPathTests
{
    // ─── Valid paths ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("/memories", "/memories")]
    [InlineData("/memories/notes.md", "/memories/notes.md")]
    [InlineData("/memories/plans/auth.md", "/memories/plans/auth.md")]
    [InlineData("/memories/", "/memories")]
    [InlineData("/memories//notes.md", "/memories/notes.md")]
    [InlineData("/memories/plans/", "/memories/plans")]
    [InlineData("  /memories/notes.md  ", "/memories/notes.md")]
    [InlineData("/memories/.recall/raven config", "/memories/.recall/raven config")]
    public void TryParse_CanonicalizesValidPaths(string raw, string expected)
    {
        Assert.True(MemoryPath.TryParse(raw, out var path, out _));
        Assert.Equal(expected, path.Value);
    }

    [Fact]
    public void Of_ReturnsCanonicalPath()
    {
        Assert.Equal("/memories/a/b.md", MemoryPath.Of("/memories/a//b.md").Value);
    }

    [Fact]
    public void Relative_MapsToBlobKeyForm()
    {
        Assert.Equal("", MemoryPath.Of("/memories").Relative);
        Assert.Equal("plans/auth.md", MemoryPath.Of("/memories/plans/auth.md").Relative);
    }

    [Fact]
    public void Default_IsRoot()
    {
        MemoryPath path = default;
        Assert.Equal("/memories", path.Value);
        Assert.True(path.IsRoot);
    }

    // ─── Traversal and escape rejection ───────────────────────────────────

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/memoriesevil/file.md")]
    [InlineData("notes.md")]
    [InlineData("memories/notes.md")]
    [InlineData("/memories/../etc/passwd")]
    [InlineData("/memories/..")]
    [InlineData("/memories/a/../../etc")]
    [InlineData("/memories/./secret")]
    [InlineData("/memories/%2e%2e/escape")]
    [InlineData("/memories/%2E%2E/escape")]
    [InlineData("/memories/a%2fb")]
    [InlineData("/memories/a%5cb")]
    [InlineData("/memories/%252e%252e/double-encoded")]
    [InlineData(@"/memories\..\escape")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_RejectsUnsafePaths(string raw)
    {
        Assert.False(MemoryPath.TryParse(raw, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Of_ThrowsOnUnsafePath()
    {
        Assert.Throws<ArgumentException>(() => MemoryPath.Of("/memories/../escape"));
    }

    [Fact]
    public void IsUnder_MatchesSelfAndDescendants()
    {
        var dir = MemoryPath.Of("/memories/plans");
        Assert.True(MemoryPath.Of("/memories/plans").IsUnder(dir));
        Assert.True(MemoryPath.Of("/memories/plans/auth.md").IsUnder(dir));
        Assert.False(MemoryPath.Of("/memories/plansX.md").IsUnder(dir));
        Assert.False(MemoryPath.Of("/memories").IsUnder(dir));
    }
}
