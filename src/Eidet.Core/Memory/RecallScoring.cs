using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Pure scoring + budgeting helpers for the recall and L1-context pipelines.
/// FadeMem-style recency curve with a 7-day half-life; importance and access frequency
/// fold in as additive weights. Type budgets enforce ENGRAM-style diversity.
/// </summary>
public static class RecallScoring
{
    public const double RecencyHalfLifeDays = 7.0;

    public static double ComputeL1Score(MemoryEntry entry, DateTime now)
    {
        var importance = (double)entry.Importance;
        var confidence = (double)entry.Confidence;

        var daysSinceCreation = Math.Max(0, (now - entry.CreatedAt).TotalDays);
        var recency = Math.Exp(-0.693 * daysSinceCreation / RecencyHalfLifeDays);

        var frequency = Math.Min(1.0, entry.AccessCount / 10.0);

        return importance * 0.3 + confidence * 0.15 + recency * 0.25 + frequency * 0.3;
    }

    public static List<MemorySearchResult> ApplyTypeBudgets(List<MemorySearchResult> results, int limit)
    {
        var insightBudget = (int)Math.Ceiling(limit * 0.40);
        var observationBudget = (int)Math.Ceiling(limit * 0.25);
        var procedureBudget = (int)Math.Ceiling(limit * 0.20);
        var heuristicBudget = Math.Max(1, limit - insightBudget - observationBudget - procedureBudget);

        var budgeted = new List<MemorySearchResult>();
        var typeCounts = new Dictionary<MemoryType, int>
        {
            [MemoryType.Insight] = 0,
            [MemoryType.Observation] = 0,
            [MemoryType.Procedure] = 0,
            [MemoryType.Heuristic] = 0,
        };

        var budgets = new Dictionary<MemoryType, int>
        {
            [MemoryType.Insight] = insightBudget,
            [MemoryType.Observation] = observationBudget,
            [MemoryType.Procedure] = procedureBudget,
            [MemoryType.Heuristic] = heuristicBudget,
        };

        foreach (var result in results.OrderByDescending(r => r.Score))
        {
            if (budgeted.Count >= limit) break;
            if (typeCounts[result.Type] < budgets[result.Type])
            {
                budgeted.Add(result);
                typeCounts[result.Type]++;
            }
        }

        foreach (var result in results.OrderByDescending(r => r.Score))
        {
            if (budgeted.Count >= limit) break;
            if (!budgeted.Contains(result))
                budgeted.Add(result);
        }

        return budgeted;
    }

    public static MemorySearchResult ToSearchResult(MemoryEntry entry, float score) => new()
    {
        Id = entry.Id,
        RepoId = entry.RepoId,
        Type = entry.Type,
        Content = entry.Content,
        Summary = entry.Summary,
        Tags = entry.Tags,
        Entities = entry.Entities,
        Importance = entry.Importance,
        OneLiner = entry.OneLiner,
        CreatedAt = entry.CreatedAt,
        Score = score,
        LayerSource = entry.LayerId,
        IsSuperseded = !entry.IsLatest,
        Drift = entry.Drift,
    };

    public static int EstimateTokens(int charCount) => (int)Math.Ceiling(charCount / 4.0);
}
