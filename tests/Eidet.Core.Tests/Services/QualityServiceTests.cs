using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class QualityServiceTests
{
    [Fact]
    public void QualityReport_DefaultsToClean()
    {
        var report = new QualityReport();
        Assert.Equal(1.0f, report.OverallScore);
        Assert.Empty(report.Issues);
        Assert.Equal(0, report.TotalMemories);
    }

    [Fact]
    public void QualityIssue_DefaultValues()
    {
        var issue = new QualityIssue();
        Assert.Equal("", issue.CheckId);
        Assert.Equal(QualitySeverity.Info, issue.Severity);
        Assert.Equal(0, issue.AffectedCount);
        Assert.Empty(issue.ExampleIds);
    }

    [Fact]
    public void QualityBreakdown_DefaultValues()
    {
        var breakdown = new QualityBreakdown();
        Assert.Empty(breakdown.TypeDistribution);
        Assert.Empty(breakdown.TopTags);
        Assert.Equal(0, breakdown.StaleCount);
        Assert.Equal(0f, breakdown.AverageImportance);
        Assert.Equal(0f, breakdown.AverageConfidence);
    }

    [Theory]
    [InlineData(QualitySeverity.Info)]
    [InlineData(QualitySeverity.Warning)]
    [InlineData(QualitySeverity.Critical)]
    public void QualitySeverity_AllValues(QualitySeverity severity)
    {
        var issue = new QualityIssue { Severity = severity };
        Assert.Equal(severity, issue.Severity);
    }

    [Fact]
    public void QualityReport_WithIssues()
    {
        var report = new QualityReport
        {
            RepoId = "test-repo",
            TotalMemories = 100,
            AnalyzedCount = 100,
            OverallScore = 0.75f,
            Issues =
            [
                new QualityIssue
                {
                    CheckId = "stale-memories",
                    Severity = QualitySeverity.Warning,
                    Title = "Stale memories",
                    AffectedCount = 25,
                },
                new QualityIssue
                {
                    CheckId = "type-imbalance",
                    Severity = QualitySeverity.Warning,
                    Title = "Type imbalance",
                    AffectedCount = 85,
                },
            ],
        };

        Assert.Equal(2, report.Issues.Count);
        Assert.Equal("stale-memories", report.Issues[0].CheckId);
        Assert.Equal(0.75f, report.OverallScore);
    }

    [Fact]
    public void QualityBreakdown_WithData()
    {
        var breakdown = new QualityBreakdown
        {
            TypeDistribution = new Dictionary<string, int>
            {
                ["Observation"] = 50,
                ["Insight"] = 30,
                ["Procedure"] = 15,
                ["Heuristic"] = 5,
            },
            TopTags = new Dictionary<string, int>
            {
                ["auth"] = 20,
                ["database"] = 15,
            },
            StaleCount = 10,
            HighFizzleCount = 3,
            LowConfidenceCount = 5,
            OrphanObservationCount = 12,
            AverageImportance = 0.55f,
            AverageConfidence = 0.65f,
        };

        Assert.Equal(4, breakdown.TypeDistribution.Count);
        Assert.Equal(50, breakdown.TypeDistribution["Observation"]);
        Assert.Equal(2, breakdown.TopTags.Count);
        Assert.Equal(10, breakdown.StaleCount);
        Assert.Equal(0.55f, breakdown.AverageImportance);
    }
}
