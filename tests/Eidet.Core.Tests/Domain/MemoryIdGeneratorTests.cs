using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Domain;

public class MemoryIdGeneratorTests
{
    [Fact]
    public void Generate_ProducesCorrectFormat()
    {
        var id = MemoryIdGenerator.Generate("P--Eidet", MemoryType.Observation, "test content", DateTime.UtcNow);

        Assert.StartsWith("memories/P--Eidet/observation/", id);
        Assert.Equal(12, id.Split('/').Last().Length);
    }

    [Fact]
    public void Generate_DeterministicForSameInput()
    {
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var id1 = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content", now);
        var id2 = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content", now);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Generate_DifferentForDifferentContent()
    {
        var now = DateTime.UtcNow;
        var id1 = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content A", now);
        var id2 = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content B", now);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Generate_IncludesTypeInPath()
    {
        var now = DateTime.UtcNow;

        Assert.Contains("/observation/", MemoryIdGenerator.Generate("r", MemoryType.Observation, "c", now));
        Assert.Contains("/insight/", MemoryIdGenerator.Generate("r", MemoryType.Insight, "c", now));
        Assert.Contains("/procedure/", MemoryIdGenerator.Generate("r", MemoryType.Procedure, "c", now));
        Assert.Contains("/heuristic/", MemoryIdGenerator.Generate("r", MemoryType.Heuristic, "c", now));
    }
}
