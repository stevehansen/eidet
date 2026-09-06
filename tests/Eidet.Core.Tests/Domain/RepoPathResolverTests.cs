using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Domain;

/// <summary>
/// The authority on repo identity for a second checkout: a worktree's memories belong to the main
/// repository, and everything that is not a worktree is left exactly as the caller passed it.
/// </summary>
public class RepoPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eidet-rpr-" + Guid.NewGuid().ToString("n"));

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Worktree_ResolvesToTheMainRepository()
    {
        var main = Dir("main");
        Directory.CreateDirectory(Path.Combine(main, ".git"));
        var wt = Dir("scratch", "wt-issues");
        File.WriteAllText(Path.Combine(wt, ".git"),
            $"gitdir: {Path.Combine(main, ".git", "worktrees", "wt-issues")}\n");

        Assert.Equal(main, RepoPathResolver.Resolve(wt));
    }

    [Fact]
    public void Worktree_ResolvesThroughAForwardSlashPointer()
    {
        // Git writes forward slashes even on Windows, which is how the real corpus was stranded.
        var main = Dir("fwd");
        var wt = Dir("fwd-wt");
        var gitDir = Path.Combine(main, ".git").Replace('\\', '/');
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {gitDir}/worktrees/pr178");

        Assert.Equal(main, RepoPathResolver.Resolve(wt));
    }

    [Fact]
    public void Worktree_ResolvesARelativePointer()
    {
        var main = Dir("rel");
        var wt = Dir("rel", "trees", "one");
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: ../../.git/worktrees/one");

        Assert.Equal(main, RepoPathResolver.Resolve(wt));
    }

    [Fact]
    public void PrimaryCheckout_IsUnchanged()
    {
        var main = Dir("primary");
        Directory.CreateDirectory(Path.Combine(main, ".git"));

        Assert.Equal(main, RepoPathResolver.Resolve(main));
    }

    [Fact]
    public void PlainDirectory_IsUnchanged()
    {
        var plain = Dir("plain");
        Assert.Equal(plain, RepoPathResolver.Resolve(plain));
    }

    [Fact]
    public void Submodule_IsUnchanged()
    {
        // A submodule redirects its gitdir too, but it is a distinct repository, not a second
        // checkout of one — its memories are its own.
        var sub = Dir("sub");
        File.WriteAllText(Path.Combine(sub, ".git"), @"gitdir: ../parent/.git/modules/sub");

        Assert.Equal(sub, RepoPathResolver.Resolve(sub));
    }

    [Theory]
    [InlineData("P--Eidet")]
    [InlineData(@"P:\does-not-exist-anywhere")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonPaths_AndMissingPaths_ComeBackUnchanged(string input)
    {
        Assert.Equal(input, RepoPathResolver.Resolve(input));
    }

    [Fact]
    public void MalformedPointer_IsUnchanged()
    {
        var broken = Dir("broken");
        File.WriteAllText(Path.Combine(broken, ".git"), "this is not a gitdir pointer");

        Assert.Equal(broken, RepoPathResolver.Resolve(broken));
    }

    [Fact]
    public void EmptyPointer_IsUnchanged()
    {
        var empty = Dir("empty-pointer");
        File.WriteAllText(Path.Combine(empty, ".git"), "gitdir:   ");

        Assert.Equal(empty, RepoPathResolver.Resolve(empty));
    }

    [Fact]
    public void Resolve_IsIdempotent()
    {
        var main = Dir("idem");
        var wt = Dir("idem-wt");
        File.WriteAllText(Path.Combine(wt, ".git"),
            $"gitdir: {Path.Combine(main, ".git", "worktrees", "w")}");

        var once = RepoPathResolver.Resolve(wt);
        Assert.Equal(once, RepoPathResolver.Resolve(once));
    }
}
