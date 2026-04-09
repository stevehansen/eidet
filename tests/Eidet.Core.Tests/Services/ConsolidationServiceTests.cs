using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class ConsolidationServiceTests
{
    [Fact]
    public void GroupByTagOverlap_SharedTag_GroupsTogether()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", ["raven", "config"]),
            MakeObs("b", ["raven", "index"]),
            MakeObs("c", ["docker", "deploy"]),
        };

        var groups = ConsolidationService.GroupByTagOverlap(obs);

        // "a" and "b" share "raven" → one group; "c" is separate
        Assert.Equal(2, groups.Count);
        var ravenGroup = groups.First(g => g.Any(e => e.Id == "a"));
        Assert.Contains(ravenGroup, e => e.Id == "b");
    }

    [Fact]
    public void GroupByTagOverlap_NoSharedTags_AllSeparate()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", ["alpha"]),
            MakeObs("b", ["beta"]),
            MakeObs("c", ["gamma"]),
        };

        var groups = ConsolidationService.GroupByTagOverlap(obs);
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void GroupByTagOverlap_TransitiveMerge()
    {
        // a shares "x" with b; b shares "y" with c → all in one group
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", ["x"]),
            MakeObs("b", ["x", "y"]),
            MakeObs("c", ["y"]),
        };

        var groups = ConsolidationService.GroupByTagOverlap(obs);
        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void GroupByTagOverlap_CaseInsensitive()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", ["RavenDB"]),
            MakeObs("b", ["ravendb"]),
        };

        var groups = ConsolidationService.GroupByTagOverlap(obs);
        Assert.Single(groups);
    }

    [Fact]
    public void GroupByTagOverlap_Empty_ReturnsEmpty()
    {
        var groups = ConsolidationService.GroupByTagOverlap([]);
        Assert.Empty(groups);
    }

    [Fact]
    public void GroupByTagOverlap_SingleEntry_SingleGroup()
    {
        var obs = new List<MemoryEntry> { MakeObs("a", ["tag"]) };
        var groups = ConsolidationService.GroupByTagOverlap(obs);
        Assert.Single(groups);
        Assert.Single(groups[0]);
    }

    [Fact]
    public void GroupByTagOverlap_NoTags_AllSeparate()
    {
        var obs = new List<MemoryEntry>
        {
            MakeObs("a", []),
            MakeObs("b", []),
        };

        var groups = ConsolidationService.GroupByTagOverlap(obs);
        Assert.Equal(2, groups.Count);
    }

    private static MemoryEntry MakeObs(string id, List<string> tags) => new()
    {
        Id = id,
        Type = MemoryType.Observation,
        Tags = tags,
        Importance = 0.5f,
    };
}
