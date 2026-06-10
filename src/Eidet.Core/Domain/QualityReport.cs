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
}
