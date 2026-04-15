namespace Eidet.Core.Domain;

public class GraphData
{
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphEdge> Edges { get; set; } = [];
}

public class GraphNode
{
    public string Id { get; set; } = "";
    public MemoryType Type { get; set; }
    public string Label { get; set; } = "";
    public float Importance { get; set; }
    public float Confidence { get; set; }
    public DateTime CreatedAt { get; set; }
    public int AccessCount { get; set; }
    public int EchoCount { get; set; }
    public int FizzleCount { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> Entities { get; set; } = [];
}

public class GraphEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Relation { get; set; } = "";
}
