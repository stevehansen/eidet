using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

public class QualityService
{
    private readonly IEidetStore _store;

    public QualityService(IEidetStore store) => _store = store;

    public async Task<QualityReport> AnalyzeAsync(string repoId, CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        var counts = await _store.GetCountsByTypeAsync(normalizedRepoId, ct);
        var totalMemories = counts.Values.Sum();

        // Fetch up to 500 for analysis
        var entries = await _store.BrowseAsync(normalizedRepoId, 0, 500, ct: ct);

        var report = new QualityReport
        {
            RepoId = normalizedRepoId,
            AnalyzedAt = DateTime.UtcNow,
            TotalMemories = totalMemories,
            AnalyzedCount = entries.Count,
        };

        if (entries.Count == 0)
            return report;

        // Run all checks
        var issues = new List<QualityIssue?>();
        var now = DateTime.UtcNow;

        issues.Add(CheckStaleMemories(entries, now));
        issues.Add(CheckHighFizzle(entries));
        issues.Add(CheckOrphanObservations(entries, now));
        issues.Add(CheckTagConcentration(entries));
        issues.Add(CheckTypeImbalance(entries));
        issues.Add(CheckLowConfidence(entries));
        issues.Add(CheckMissingEntities(entries));
        issues.Add(CheckConflicts(entries));

        report.Issues = issues.Where(i => i != null).Cast<QualityIssue>().ToList();

        // Compute overall score
        var score = 1.0f;
        foreach (var issue in report.Issues)
        {
            score -= issue.Severity switch
            {
                QualitySeverity.Critical => 0.15f,
                QualitySeverity.Warning => 0.08f,
                QualitySeverity.Info => 0.02f,
                _ => 0f,
            };
        }
        report.OverallScore = Math.Clamp(score, 0f, 1f);

        // Compute breakdown
        report.Breakdown = ComputeBreakdown(entries, now);

        return report;
    }

    private static QualityIssue? CheckStaleMemories(List<MemoryEntry> entries, DateTime now)
    {
        var stale = entries.Where(e =>
            (e.LastAccessedAt == null && (now - e.CreatedAt).TotalDays > 30) ||
            (e.LastAccessedAt != null && (now - e.LastAccessedAt.Value).TotalDays > 60))
            .ToList();

        if (stale.Count == 0) return null;

        var ratio = (float)stale.Count / entries.Count;
        return new QualityIssue
        {
            CheckId = "stale-memories",
            Severity = ratio > 0.30f ? QualitySeverity.Critical : QualitySeverity.Warning,
            Title = "Stale memories",
            Description = $"{stale.Count} memories ({ratio:P0}) have not been accessed recently",
            AffectedCount = stale.Count,
            ExampleIds = stale.Take(5).Select(e => e.Id).ToList(),
        };
    }

    private static QualityIssue? CheckHighFizzle(List<MemoryEntry> entries)
    {
        var highFizzle = entries.Where(e => e.FizzleCount >= 3 && e.FizzleCount > e.EchoCount * 2).ToList();

        if (highFizzle.Count == 0) return null;

        return new QualityIssue
        {
            CheckId = "high-fizzle",
            Severity = QualitySeverity.Warning,
            Title = "High-fizzle memories",
            Description = $"{highFizzle.Count} memories are recalled but rarely useful (fizzle >> echo)",
            AffectedCount = highFizzle.Count,
            ExampleIds = highFizzle.Take(5).Select(e => e.Id).ToList(),
        };
    }

    private static QualityIssue? CheckOrphanObservations(List<MemoryEntry> entries, DateTime now)
    {
        var orphans = entries.Where(e =>
            e.Type == MemoryType.Observation &&
            e.DerivedFrom.Count == 0 &&
            (now - e.CreatedAt).TotalDays > 14)
            .ToList();

        if (orphans.Count == 0) return null;

        var ratio = (float)orphans.Count / entries.Count;
        return new QualityIssue
        {
            CheckId = "orphan-observations",
            Severity = ratio >= 0.20f ? QualitySeverity.Warning : QualitySeverity.Info,
            Title = "Orphan observations",
            Description = $"{orphans.Count} observations are older than 14 days and haven't been consolidated",
            AffectedCount = orphans.Count,
            ExampleIds = orphans.Take(5).Select(e => e.Id).ToList(),
        };
    }

    private static QualityIssue? CheckTagConcentration(List<MemoryEntry> entries)
    {
        var allTags = entries.SelectMany(e => e.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count());

        var threshold = entries.Count * 0.40;
        var concentrated = allTags.Where(kv => kv.Value > threshold).ToList();

        if (concentrated.Count == 0) return null;

        var tagList = string.Join(", ", concentrated.Select(kv => $"'{kv.Key}' ({kv.Value})"));
        return new QualityIssue
        {
            CheckId = "tag-concentration",
            Severity = QualitySeverity.Info,
            Title = "Tag concentration",
            Description = $"Tags on >40% of memories: {tagList}. Consider more specific tagging.",
            AffectedCount = concentrated.Sum(kv => kv.Value),
        };
    }

    private static QualityIssue? CheckTypeImbalance(List<MemoryEntry> entries)
    {
        var byType = entries.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        var obsCount = byType.GetValueOrDefault(MemoryType.Observation);
        var insightCount = byType.GetValueOrDefault(MemoryType.Insight);

        if (entries.Count < 10) return null;

        if (obsCount > entries.Count * 0.80 || (insightCount == 0 && obsCount >= 10))
        {
            return new QualityIssue
            {
                CheckId = "type-imbalance",
                Severity = QualitySeverity.Warning,
                Title = "Type imbalance",
                Description = $"Observations dominate ({obsCount}/{entries.Count}). Run consolidation to distill insights.",
                AffectedCount = obsCount,
            };
        }

        return null;
    }

    private static QualityIssue? CheckLowConfidence(List<MemoryEntry> entries)
    {
        var lowConf = entries.Where(e => e.Confidence < 0.3f).ToList();

        if (lowConf.Count == 0) return null;

        var ratio = (float)lowConf.Count / entries.Count;
        if (ratio < 0.15f) return null;

        return new QualityIssue
        {
            CheckId = "low-confidence",
            Severity = QualitySeverity.Warning,
            Title = "Low-confidence memories",
            Description = $"{lowConf.Count} memories ({ratio:P0}) have confidence below 0.3",
            AffectedCount = lowConf.Count,
            ExampleIds = lowConf.Take(5).Select(e => e.Id).ToList(),
        };
    }

    private static QualityIssue? CheckMissingEntities(List<MemoryEntry> entries)
    {
        var missing = entries.Where(e => e.Entities.Count == 0 && e.Content.Length > 50).ToList();

        if (missing.Count == 0) return null;

        return new QualityIssue
        {
            CheckId = "missing-entities",
            Severity = QualitySeverity.Info,
            Title = "Missing entity extraction",
            Description = $"{missing.Count} memories have no extracted entities. Run maintenance to backfill.",
            AffectedCount = missing.Count,
            ExampleIds = missing.Take(5).Select(e => e.Id).ToList(),
        };
    }

    private static QualityIssue? CheckConflicts(List<MemoryEntry> entries)
    {
        // Check top 50 by importance for potential conflicts
        var top = entries.OrderByDescending(e => e.Importance).Take(50).ToList();
        var conflicts = new List<(string A, string B)>();

        for (var i = 0; i < top.Count && conflicts.Count < 5; i++)
        {
            for (var j = i + 1; j < top.Count && conflicts.Count < 5; j++)
            {
                if (top[i].Type != top[j].Type) continue;
                var sim = Eidet.Core.Text.WordSimilarity.Compute(top[i].Content, top[j].Content);
                if (sim is >= 0.50f and < 0.84f)
                    conflicts.Add((top[i].Id, top[j].Id));
            }
        }

        if (conflicts.Count == 0) return null;

        return new QualityIssue
        {
            CheckId = "potential-conflicts",
            Severity = QualitySeverity.Warning,
            Title = "Potential conflicts",
            Description = $"{conflicts.Count} pairs of memories have similar but not identical content — may conflict",
            AffectedCount = conflicts.Count,
            ExampleIds = conflicts.SelectMany(c => new[] { c.A, c.B }).Distinct().Take(5).ToList(),
        };
    }

    private static QualityBreakdown ComputeBreakdown(List<MemoryEntry> entries, DateTime now)
    {
        var byType = entries.GroupBy(e => e.Type).ToDictionary(g => g.Key.ToString(), g => g.Count());
        var topTags = entries.SelectMany(e => e.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToDictionary(g => g.Key, g => g.Count());

        return new QualityBreakdown
        {
            TypeDistribution = byType,
            TopTags = topTags,
            StaleCount = entries.Count(e =>
                (e.LastAccessedAt == null && (now - e.CreatedAt).TotalDays > 30) ||
                (e.LastAccessedAt != null && (now - e.LastAccessedAt.Value).TotalDays > 60)),
            HighFizzleCount = entries.Count(e => e.FizzleCount >= 3 && e.FizzleCount > e.EchoCount * 2),
            LowConfidenceCount = entries.Count(e => e.Confidence < 0.3f),
            OrphanObservationCount = entries.Count(e =>
                e.Type == MemoryType.Observation && e.DerivedFrom.Count == 0 && (now - e.CreatedAt).TotalDays > 14),
            AverageImportance = entries.Count > 0 ? entries.Average(e => e.Importance) : 0,
            AverageConfidence = entries.Count > 0 ? entries.Average(e => e.Confidence) : 0,
        };
    }
}
