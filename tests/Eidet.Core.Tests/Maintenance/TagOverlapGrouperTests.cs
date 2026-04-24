using Eidet.Core.Domain;
using Eidet.Core.Maintenance;

namespace Eidet.Core.Tests.Maintenance;

public class TagOverlapGrouperTests
{
    [Fact]
    public void SharedTag_GroupsTogether()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", ["raven", "config"]),
            MakeObs("b", ["raven", "index"]),
            MakeObs("c", ["docker", "deploy"]),
        };

        var groups = TagOverlapGrouper.Group(obs);

        Assert.Equal(2, groups.Count);
        var ravenGroup = groups.First(g => g.Any(e => e.Id == "a"));
        Assert.Contains(ravenGroup, e => e.Id == "b");
    }

    [Fact]
    public void NoSharedTags_AllSeparate()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", ["alpha"]),
            MakeObs("b", ["beta"]),
            MakeObs("c", ["gamma"]),
        };

        Assert.Equal(3, TagOverlapGrouper.Group(obs).Count);
    }

    [Fact]
    public void TransitiveMerge()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", ["x"]),
            MakeObs("b", ["x", "y"]),
            MakeObs("c", ["y"]),
        };

        var groups = TagOverlapGrouper.Group(obs);
        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void CaseInsensitive()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", ["RavenDB"]),
            MakeObs("b", ["ravendb"]),
        };

        Assert.Single(TagOverlapGrouper.Group(obs));
    }

    [Fact]
    public void Empty_ReturnsEmpty() =>
        Assert.Empty(TagOverlapGrouper.Group([]));

    [Fact]
    public void SingleEntry_SingleGroup()
    {
        var groups = TagOverlapGrouper.Group([MakeObs("a", ["tag"])]);
        Assert.Single(groups);
        Assert.Single(groups[0]);
    }

    [Fact]
    public void NoTags_AllSeparate()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", []),
            MakeObs("b", []),
        };
        Assert.Equal(2, TagOverlapGrouper.Group(obs).Count);
    }

    private static MemoryEntry MakeObs(string id, List<string> tags) => new()
    {
        Id = id,
        Type = MemoryType.Observation,
        Tags = tags,
        Importance = 0.5f,
    };
}
