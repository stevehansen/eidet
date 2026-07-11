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

    // ─── Drift-flagged analysis ───────────────────────────────────────────

    private static MemoryEntry MakeDriftEntry(string id, DriftVerdictKind? verdict, bool isLatest = true) => new()
    {
        Id = $"memories/repo-a/insight/{id}",
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = $"deployment uses argo cd via gitops {id}",
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = isLatest,
        Importance = 0.6f,
        Drift = verdict is null ? null : new DriftReview { Verdict = verdict.Value, ReviewedAt = DateTime.UtcNow },
    };

    [Fact]
    public async Task Analyze_DriftFlaggedEntries_ReportsIssueWithExamplesAndCount()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(MakeDriftEntry("stale-1", DriftVerdictKind.Stale));
        await store.StoreAsync(MakeDriftEntry("vague-1", DriftVerdictKind.Vague));
        await store.StoreAsync(MakeDriftEntry("ok-1", DriftVerdictKind.Ok));
        await store.StoreAsync(MakeDriftEntry("unreviewed-1", null));
        await store.StoreAsync(MakeDriftEntry("superseded-1", DriftVerdictKind.Contradicted, isLatest: false));

        var report = await new QualityService(store).AnalyzeAsync("repo-a");

        var issue = report.Issues.Single(i => i.CheckId == "drift-flagged");
        Assert.Equal(QualitySeverity.Warning, issue.Severity);
        Assert.Equal(2, issue.AffectedCount);
        Assert.Equal(2, issue.ExampleIds.Count);
        Assert.Contains("memories/repo-a/insight/stale-1", issue.ExampleIds);
        Assert.Contains("memories/repo-a/insight/vague-1", issue.ExampleIds);
        Assert.Equal(2, report.Breakdown.DriftFlaggedCount);
    }

    [Fact]
    public async Task Analyze_NoDriftFlags_NoIssueAndZeroCount()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(MakeDriftEntry("ok-1", DriftVerdictKind.Ok));
        await store.StoreAsync(MakeDriftEntry("unreviewed-1", null));

        var report = await new QualityService(store).AnalyzeAsync("repo-a");

        Assert.DoesNotContain(report.Issues, i => i.CheckId == "drift-flagged");
        Assert.Equal(0, report.Breakdown.DriftFlaggedCount);
    }

    // ─── Reflection echo-rate health ──────────────────────────────────────

    private static MemoryEntry MakeReflectedEntry(string id, int echo, int fizzle, string source = "reflection") => new()
    {
        Id = $"memories/repo-a/insight/{id}",
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = $"reflected insight {id}",
        Source = source,
        Provenance = MemoryProvenance.Reflection,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = 0.5f,
        EchoCount = echo,
        FizzleCount = fizzle,
    };

    [Fact]
    public async Task Analyze_NoReflectedMemories_ReflectionHealthIsNull()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(MakeDriftEntry("plain-1", null));

        var report = await new QualityService(store).AnalyzeAsync("repo-a");

        Assert.Null(report.Breakdown.Reflection);
        Assert.DoesNotContain(report.Issues, i => i.CheckId == "reflection-underperforming");
    }

    [Fact]
    public async Task Analyze_ReflectedMemories_ComputesEchoRateAndCounts()
    {
        var store = new InMemoryEidetStore();
        for (var i = 0; i < 3; i++) await store.StoreAsync(MakeReflectedEntry($"echoed-{i}", echo: 2, fizzle: 0));
        for (var i = 0; i < 3; i++) await store.StoreAsync(MakeReflectedEntry($"young-{i}", echo: 0, fizzle: 0));

        var report = await new QualityService(store).AnalyzeAsync("repo-a");

        var rh = Assert.IsType<ReflectionHealth>(report.Breakdown.Reflection);
        Assert.Equal(6, rh.Total);
        Assert.Equal(3, rh.Echoed);
        Assert.Equal(3, rh.Untouched);
        Assert.Equal(0, rh.NetNegative);
        Assert.Equal(0.5f, rh.EchoRate);
        // Healthy set: no underperformance alarm.
        Assert.DoesNotContain(report.Issues, i => i.CheckId == "reflection-underperforming");
    }

    [Fact]
    public async Task Analyze_NetNegativeReflectedMemories_RaisesUnderperformingWarning()
    {
        var store = new InMemoryEidetStore();
        for (var i = 0; i < 4; i++) await store.StoreAsync(MakeReflectedEntry($"dud-{i}", echo: 0, fizzle: 2)); // net-negative
        await store.StoreAsync(MakeReflectedEntry("good-1", echo: 3, fizzle: 0));                                // useful

        var report = await new QualityService(store).AnalyzeAsync("repo-a");

        var issue = report.Issues.Single(i => i.CheckId == "reflection-underperforming");
        Assert.Equal(QualitySeverity.Warning, issue.Severity);
        Assert.Equal(4, issue.AffectedCount);
        var rh = Assert.IsType<ReflectionHealth>(report.Breakdown.Reflection);
        Assert.Equal(5, rh.Total);
        Assert.Equal(4, rh.NetNegative);
        Assert.Equal(0.2f, rh.EchoRate);
    }

    [Fact]
    public async Task Analyze_FewNetNegativeReflectedMemories_DoesNotAlarm()
    {
        var store = new InMemoryEidetStore();
        // Only 2 net-negative — below the min-evidence floor, so no alarm even though they exist.
        for (var i = 0; i < 2; i++) await store.StoreAsync(MakeReflectedEntry($"dud-{i}", echo: 0, fizzle: 2));
        for (var i = 0; i < 4; i++) await store.StoreAsync(MakeReflectedEntry($"good-{i}", echo: 2, fizzle: 0));

        var report = await new QualityService(store).AnalyzeAsync("repo-a");

        Assert.DoesNotContain(report.Issues, i => i.CheckId == "reflection-underperforming");
        Assert.Equal(6, report.Breakdown.Reflection!.Total);
    }
}
