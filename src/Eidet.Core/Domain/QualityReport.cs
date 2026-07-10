namespace Eidet.Core.Domain;

public class QualityReport
{
    public string RepoId { get; set; } = "";
    public DateTime AnalyzedAt { get; set; }
    public int TotalMemories { get; set; }
    public int AnalyzedCount { get; set; }
    public float OverallScore { get; set; } = 1.0f;
    public List<QualityIssue> Issues { get; set; } = [];
    public QualityBreakdown Breakdown { get; set; } = new();
}

public class QualityIssue
{
    public string CheckId { get; set; } = "";
    public QualitySeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int AffectedCount { get; set; }
    public List<string> ExampleIds { get; set; } = [];
}

public enum QualitySeverity { Info, Warning, Critical }

public class QualityBreakdown
{
    public Dictionary<string, int> TypeDistribution { get; set; } = new();
    public Dictionary<string, int> TopTags { get; set; } = new();
    public int StaleCount { get; set; }
    public int HighFizzleCount { get; set; }
    public int LowConfidenceCount { get; set; }
    public int OrphanObservationCount { get; set; }
    public int DriftFlaggedCount { get; set; }
    public float AverageImportance { get; set; }
    public float AverageConfidence { get; set; }

    /// <summary>Empirical health of Reflector-minted memories, or null when the repo has none.
    /// The echo rate is the gate for deciding whether to keep the (dormant-by-default) Reflector on.</summary>
    public ReflectionHealth? Reflection { get; set; }
}

/// <summary>
/// How well the Reflector's synthesized memories are earning their keep. Scoped by <c>Source == "reflection"</c>
/// (not provenance — the anti-laundering stamp rewrites provenance on some reflected memories, but the source
/// tag is always set), so it counts every reflected memory, laundered lineage included.
/// </summary>
public class ReflectionHealth
{
    public int Total { get; set; }        // reflected memories in the analyzed sample (latest only)
    public int Echoed { get; set; }       // earned at least one echo — proved useful
    public int NetNegative { get; set; }  // FizzleCount > EchoCount — recalled but unhelpful
    public int Untouched { get; set; }    // no feedback yet — too young to judge
    public float EchoRate { get; set; }   // Echoed / Total, in [0,1]; 0 when Total == 0
}
