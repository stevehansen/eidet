using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Domain;

public class GraphDataTests
{
    [Fact]
    public void GraphData_Defaults()
    {
        var data = new GraphData();
        Assert.Empty(data.Nodes);
        Assert.Empty(data.Edges);
    }

    [Fact]
    public void GraphNode_Defaults()
    {
        var node = new GraphNode();
        Assert.Equal("", node.Id);
        Assert.Equal("", node.Label);
        Assert.Equal(MemoryType.Observation, node.Type);
        Assert.Equal(0f, node.Importance);
        Assert.Empty(node.Tags);
    }

    [Fact]
    public void GraphEdge_Defaults()
    {
        var edge = new GraphEdge();
        Assert.Equal("", edge.From);
        Assert.Equal("", edge.To);
        Assert.Equal("", edge.Relation);
    }

    [Fact]
    public void GraphData_WithNodes()
    {
        var data = new GraphData
        {
            Nodes =
            [
                new GraphNode { Id = "n1", Type = MemoryType.Insight, Label = "Test insight", Importance = 0.8f },
                new GraphNode { Id = "n2", Type = MemoryType.Observation, Label = "Test obs", Importance = 0.5f },
            ],
            Edges =
            [
                new GraphEdge { From = "n1", To = "n2", Relation = "derived" },
            ],
        };

        Assert.Equal(2, data.Nodes.Count);
        Assert.Single(data.Edges);
        Assert.Equal("derived", data.Edges[0].Relation);
    }
}
