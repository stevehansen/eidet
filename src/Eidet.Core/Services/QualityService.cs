using Eidet.Core.Domain;
using Eidet.Core.Integrity;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

public class QualityService
{
    private readonly IEidetStore _store;
    private readonly IIntegrityAuditor? _auditor;

    // The auditor is optional so existing callers / test fakes are unaffected; when wired, a post-forget
    // leak surfaces on GET /api/eidet/quality as a Critical issue with zero new UI plumbing.
    public QualityService(IEidetStore store, IIntegrityAuditor? auditor = null)
    {
        _store = store;
        _auditor = auditor;
    }

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
        issues.Add(CheckDriftFlagged(entries));
        issues.Add(CheckReflectionHealth(entries));
        issues.Add(CheckMergeRejected(entries));
        issues.AddRange(await CheckIntegrityAsync(normalizedRepoId, ct));

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

    // Runtime integrity verification (#37, #80). ONE auditor call feeds four distinct dashboard rows — a
    // broken content commitment is not "a forgotten memory still reachable", and folding them together
    // would hide which invariant actually failed. The auditor is read-only, so a quality run never mutates
    // (repair belongs to the nightly maintenance stage). Best-effort: a store hiccup here must not fail the
    // whole quality analysis. No-op when the auditor isn't wired.
    private async Task<List<QualityIssue>> CheckIntegrityAsync(string repoId, CancellationToken ct)
    {
        if (_auditor is null) return [];

        IntegrityReport report;
        try
        {
            report = await _auditor.VerifyAsync(repoId, ct);
        }
        catch
        {
            return [];
        }

        var issues = new List<QualityIssue>();

        // A probe that threw is a COVERAGE gap, not a data defect: nothing was observed to be wrong, and
        // nothing was confirmed right either. Reported first (it qualifies every row below it) and never
        // folded into them — attributing it to, say, forget-leak would render an unrun check as N leaked
        // memories, inventing a Critical out of a store hiccup.
        var unprobed = report.Findings.Where(f => f.ProbeFailed).ToList();
        if (unprobed.Count > 0)
            issues.Add(new QualityIssue
            {
                CheckId = "integrity-unprobed",
                Severity = QualitySeverity.Warning,
                Title = "Integrity checks did not complete",
                Description = $"{unprobed.Count} integrity check(s) failed to run " +
                              $"({string.Join(", ", unprobed.Select(f => f.Check).Distinct())}), so their verdict is " +
                              "unknown for this sample. The rows below cover only the checks that completed.",
                AffectedCount = unprobed.Count,
                ExampleIds = [],
            });

        void Add(
            string checkId, QualitySeverity severity, string title,
            IReadOnlyList<IntegrityCheck> checks, Func<List<IntegrityFinding>, string> describe)
        {
            var matched = report.Findings.Where(f => !f.ProbeFailed && checks.Contains(f.Check)).ToList();
            if (matched.Count == 0) return;
            issues.Add(new QualityIssue
            {
                CheckId = checkId,
                Severity = severity,
                Title = title,
                Description = describe(matched),
                AffectedCount = matched.Count,
                ExampleIds = matched
                    .Select(f => f.MemoryId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Take(5).ToList(),
            });
        }

        Add("forget-leak", QualitySeverity.Critical, "Forgotten memories still reachable",
            [
                IntegrityCheck.Recall, IntegrityCheck.ContextL1, IntegrityCheck.CrossRepoSearch,
                IntegrityCheck.GraphNeighbor, IntegrityCheck.EntityNeighbor, IntegrityCheck.DuplicateDetection,
            ],
            m => $"{m.Count} forgotten/superseded memories still surface via: " +
                 $"{string.Join(", ", m.Select(f => f.Check).Distinct())}. These should be invisible to every read path.");

        Add("commitment-broken", QualitySeverity.Critical, "Memory content no longer matches its id",
            [IntegrityCheck.BrokenCommitment],
            m => $"{m.Count} memories were rewritten in place rather than superseded, so the content commitment in their id no longer verifies. Recall de-boosts them heavily; they are never hidden.");

        Add("provenance-unknown", QualitySeverity.Warning, "Unestablished provenance",
            [IntegrityCheck.UnknownProvenance],
            m => $"{m.Count} memories have no established provenance (pre-field documents, or a source this build does not recognize), so they carry the import trust floor until the nightly stage repairs them or an echo lifts them.");

        Add("lineage-drift", QualitySeverity.Warning, "Lineage citations no longer resolve",
            [IntegrityCheck.DanglingCitation, IntegrityCheck.AmendedCitation],
            m => $"{m.Count} memories cite a source that is missing or whose content was amended after the citation was made — the lineage no longer describes the text it was derived from.");

        return issues;
    }

    // Merges the recall-consistency guard withheld (#39). Informational, not a defect: nothing was
    // forgotten — a near-dup pair was deliberately kept because folding it would lose retrievability.
    // Reads the LastMergeRejectedAt stamp already on the browsed entries — no new query/collection.
    private static QualityIssue? CheckMergeRejected(List<MemoryEntry> entries)
    {
        var rejected = entries.Where(e => e.LastMergeRejectedAt is not null && e.IsLatest).ToList();
        if (rejected.Count == 0) return null;

        return new QualityIssue
        {
            CheckId = "merge-rejected",
            Severity = QualitySeverity.Info,
            Title = "Merges withheld for recall consistency",
            Description = $"{rejected.Count} memories were kept out of a dedup/consolidation merge because the survivor would not have surfaced for their retrieval intent — nothing was forgotten.",
            AffectedCount = rejected.Count,
            ExampleIds = rejected.Take(5).Select(e => e.Id).ToList(),
        };
    }

    private static QualityIssue? CheckDriftFlagged(List<MemoryEntry> entries)
    {
        var flagged = entries.Where(e => e.Drift is { Verdict: not DriftVerdictKind.Ok } && e.IsLatest).ToList();

        if (flagged.Count == 0) return null;

        return new QualityIssue
        {
            CheckId = "drift-flagged",
            Severity = QualitySeverity.Warning,
            Title = "Drift-flagged memories",
            Description = $"{flagged.Count} memories were flagged by drift review as stale, contradicted, or vague — SuggestedFix proposals await human review",
            AffectedCount = flagged.Count,
            ExampleIds = flagged.Take(5).Select(e => e.Id).ToList(),
        };
    }

    // Reflected memories are tagged Source=="reflection" (see ReflectionEngine). We alarm only on
    // demonstrated harm — net-negative reflected memories that outnumber the useful ones — never on
    // youth, so a freshly-enabled Reflector whose memories are simply untouched doesn't trip it.
    private static QualityIssue? CheckReflectionHealth(List<MemoryEntry> entries)
    {
        var reflected = entries.Where(e => e.Source == "reflection" && e.IsLatest).ToList();
        if (reflected.Count < 5) return null; // too little evidence to judge the Reflector

        var netNegative = reflected.Where(e => e.FizzleCount > e.EchoCount).ToList();
        var echoed = reflected.Count(e => e.EchoCount > 0);
        if (netNegative.Count < 3 || netNegative.Count <= echoed) return null;

        var echoRate = (float)echoed / reflected.Count;
        return new QualityIssue
        {
            CheckId = "reflection-underperforming",
            Severity = QualitySeverity.Warning,
            Title = "Reflected memories underperforming",
            Description = $"{netNegative.Count} of {reflected.Count} reflection-minted memories run net-negative (echo rate {echoRate:P0}). Consider disabling the Reflector or tightening its residue filters.",
            AffectedCount = netNegative.Count,
            ExampleIds = netNegative.Take(5).Select(e => e.Id).ToList(),
        };
    }

    private static ReflectionHealth? ComputeReflectionHealth(List<MemoryEntry> entries)
    {
        var reflected = entries.Where(e => e.Source == "reflection" && e.IsLatest).ToList();
        if (reflected.Count == 0) return null; // surface the metric only once the Reflector is producing

        var echoed = reflected.Count(e => e.EchoCount > 0);
        return new ReflectionHealth
        {
            Total = reflected.Count,
            Echoed = echoed,
            NetNegative = reflected.Count(e => e.FizzleCount > e.EchoCount),
            Untouched = reflected.Count(e => e.EchoCount == 0 && e.FizzleCount == 0),
            EchoRate = (float)echoed / reflected.Count,
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
            DriftFlaggedCount = entries.Count(e => e.Drift is { Verdict: not DriftVerdictKind.Ok } && e.IsLatest),
            AverageImportance = entries.Count > 0 ? entries.Average(e => e.Importance) : 0,
            AverageConfidence = entries.Count > 0 ? entries.Average(e => e.Confidence) : 0,
            Reflection = ComputeReflectionHealth(entries),
        };
    }
}
