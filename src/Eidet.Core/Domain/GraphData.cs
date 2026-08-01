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

/// <summary>
/// Whether an edge's endpoints still resolve. <see cref="Missing"/> means the cited memory could not be
/// found at all — a dangling lineage citation. Emitted rather than dropped so an unresolvable citation is
/// distinguishable from no citation (#80); a citation that merely falls outside the requested graph window
/// stays <see cref="Ok"/>.
/// </summary>
public enum GraphEdgeStatus { Ok, Missing }

public class GraphEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Relation { get; set; } = "";
    public GraphEdgeStatus Status { get; set; } = GraphEdgeStatus.Ok;
}
