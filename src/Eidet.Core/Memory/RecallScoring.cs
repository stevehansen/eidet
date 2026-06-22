using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Storage;

namespace Eidet.Core.Memory;

/// <summary>
/// Tuning knobs for hybrid fusion. <see cref="Alpha"/> blends lexical vs vector arms
/// (lexical weight); <see cref="Kappa"/> scales the UCB exploration bonus; <see cref="TotalN"/>
/// is the candidate-pool feedback total (Σ Echo+Fizzle) the caller supplies for the UCB term.
/// </summary>
public readonly record struct RecallWeights(double Alpha, double Kappa, long TotalN)
{
    public static RecallWeights Default => new(Alpha: 0.5, Kappa: 0.3, TotalN: 0);
}

/// <summary>Per-candidate fusion breakdown: normalized arm scores, recency + UCB components, and the total.</summary>
public readonly record struct FusedCandidate(
    MemoryEntry Entry, double Lex, double Vec, double Recency, double Ucb, double Fused);

/// <summary>
/// Pure scoring + budgeting helpers for the recall and L1-context pipelines.
/// <see cref="Fuse"/> is the single home of the hybrid recall ranking math (min-max-normalized
/// lexical+vector blend + UCB exploration + dual-clock FadeMem recency). FadeMem-style recency
/// folds both creation and last-access clocks; type budgets enforce ENGRAM-style diversity.
/// </summary>
public static class RecallScoring
{
    public const double RecencyHalfLifeDays = 7.0;

    public static double ComputeL1Score(MemoryEntry entry, DateTime now)
    {
        var importance = (double)entry.Importance;
        var confidence = (double)entry.Confidence;

        // Dual-clock recency on the L1 wake-up curve: a memory accessed recently stays fresh even
        // if created long ago, so the more-recent clock dominates (null LastAccessedAt → creation
        // only). This keeps the fixed 7-day half-life the wake-up context has always used — distinct
        // from the per-type FadeMem curve recall fusion uses (see Fuse); the two surfaces rank for
        // different purposes and are deliberately not unified.
        var recency = SevenDayRecency(entry.CreatedAt, now);
        if (entry.LastAccessedAt is { } accessed)
            recency = Math.Max(recency, SevenDayRecency(accessed, now));

        var frequency = Math.Min(1.0, entry.AccessCount / 10.0);

        return importance * 0.3 + confidence * 0.15 + recency * 0.25 + frequency * 0.3;
    }

    private static double SevenDayRecency(DateTime clock, DateTime now) =>
        Math.Exp(-0.693 * Math.Max(0, (now - clock).TotalDays) / RecencyHalfLifeDays);

    /// <summary>
    /// The single source of the hybrid recall ranking. Min-max-normalizes each arm independently
    /// (empty arm → 0 for every candidate; single-candidate or all-equal arm → 1.0 to dodge a
    /// divide-by-zero), outer-joins the two arms by <see cref="MemoryEntry.Id"/>, then scores each
    /// candidate as <c>Alpha·normLex + (1-Alpha)·normVec + UCB + recency</c> where UCB =
    /// <c>Kappa·sqrt(ln(TotalN+1)/(Echo+Fizzle+1))</c> rewards rarely-surfaced memories and recency
    /// is the per-type dual-clock FadeMem curve. Returns candidates sorted by fused score descending.
    /// </summary>
    public static List<FusedCandidate> Fuse(
        IReadOnlyList<ScoredHit> lex, IReadOnlyList<ScoredHit> vec, RecallWeights w, DateTime now)
    {
        var normLex = Normalize(lex);
        var normVec = Normalize(vec);

        var entries = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in lex) entries.TryAdd(hit.Entry.Id, hit.Entry);
        foreach (var hit in vec) entries.TryAdd(hit.Entry.Id, hit.Entry);

        var lnN = Math.Log(w.TotalN + 1);

        var fused = new List<FusedCandidate>(entries.Count);
        foreach (var (id, entry) in entries)
        {
            var l = normLex.GetValueOrDefault(id);
            var v = normVec.GetValueOrDefault(id);
            var ucb = Ucb(entry, w, lnN);
            var recency = FadeMemCurve.Recency(entry.CreatedAt, entry.LastAccessedAt, now, entry.Type);
            var score = w.Alpha * l + (1 - w.Alpha) * v + ucb + recency;
            fused.Add(new FusedCandidate(entry, l, v, recency, ucb, score));
        }

        fused.Sort((a, b) => b.Fused.CompareTo(a.Fused));
        return fused;
    }

    /// <summary>UCB exploration bonus: <c>Kappa·sqrt(ln(TotalN+1)/(Echo+Fizzle+1))</c>, the single home of
    /// the exploration math shared by <see cref="Fuse"/> and <see cref="ExpandNeighbors"/>.</summary>
    private static double Ucb(MemoryEntry entry, RecallWeights w, double lnN) =>
        w.Kappa * Math.Sqrt(lnN / (entry.EchoCount + entry.FizzleCount + 1));

    /// <summary>
    /// Expands the fused pool with link-reachable neighbors that compete via damped inheritance:
    /// a neighbor not already in the pool inherits parentFused * <paramref name="neighborDecay"/> plus
    /// its own recency+UCB, so related-but-unsurfaced memories get a real shot at the budget without
    /// swamping direct hits. Bounded: only the top <paramref name="parentTopK"/> candidates spread, total
    /// new neighbors capped at <paramref name="maxNeighbors"/>, one hop. <paramref name="resolve"/> returns
    /// the neighbor entry for a link target id (null if out of scope / missing). Neighbors enter with
    /// normalized Lex/Vec = 0 (they are in neither arm); their inherited association is carried in the
    /// <see cref="FusedCandidate.Fused"/> total. Pure, deterministic, re-sorted by fused descending.
    /// </summary>
    public static List<FusedCandidate> ExpandNeighbors(
        IReadOnlyList<FusedCandidate> fused, Func<string, MemoryEntry?> resolve,
        RecallWeights w, DateTime now, int parentTopK = 10, int maxNeighbors = 5, double neighborDecay = 0.5)
    {
        var lnN = Math.Log(w.TotalN + 1);
        var present = new HashSet<string>(fused.Select(c => c.Entry.Id), StringComparer.OrdinalIgnoreCase);
        var added = new List<FusedCandidate>();

        // Parents are already sorted by fused descending (Fuse re-sorts); take the strongest few so the
        // spreading activation flows from the most-relevant hits, not from low-ranked noise.
        foreach (var parent in fused.Take(parentTopK))
        {
            if (added.Count >= maxNeighbors) break;
            foreach (var link in parent.Entry.Links)
            {
                if (added.Count >= maxNeighbors) break;
                var targetId = link.TargetMemoryId;
                if (string.IsNullOrEmpty(targetId) || present.Contains(targetId)) continue;

                var neighbor = resolve(targetId);
                if (neighbor is null) continue;

                var ucb = Ucb(neighbor, w, lnN);
                var recency = FadeMemCurve.Recency(neighbor.CreatedAt, neighbor.LastAccessedAt, now, neighbor.Type);
                var score = parent.Fused * neighborDecay + ucb + recency;
                added.Add(new FusedCandidate(neighbor, Lex: 0, Vec: 0, recency, ucb, score));
                present.Add(targetId); // dedup neighbors against each other, not just against the pool
            }
        }

        if (added.Count == 0)
        {
            var copy = new List<FusedCandidate>(fused);
            copy.Sort((a, b) => b.Fused.CompareTo(a.Fused));
            return copy;
        }

        var expanded = new List<FusedCandidate>(fused.Count + added.Count);
        expanded.AddRange(fused);
        expanded.AddRange(added);
        expanded.Sort((a, b) => b.Fused.CompareTo(a.Fused));
        return expanded;
    }

    /// <summary>Convenience projection: fuse, then map to <see cref="MemorySearchResult"/> carrying the fused score.</summary>
    public static List<MemorySearchResult> FuseAndScore(
        IReadOnlyList<ScoredHit> lex, IReadOnlyList<ScoredHit> vec, RecallWeights w, DateTime now) =>
        Fuse(lex, vec, w, now).Select(c => ToSearchResult(c.Entry, (float)c.Fused)).ToList();

    /// <summary>
    /// Min-max normalizes an arm's raw scores to 0..1 keyed by entry id. Empty arm → empty map
    /// (every candidate degrades to 0 for this arm); all-equal scores (max==min, includes the
    /// single-candidate case) → 1.0 to avoid a divide-by-zero.
    /// </summary>
    private static Dictionary<string, double> Normalize(IReadOnlyList<ScoredHit> arm)
    {
        var map = new Dictionary<string, double>(arm.Count, StringComparer.OrdinalIgnoreCase);
        if (arm.Count == 0) return map;

        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var hit in arm)
        {
            if (hit.Score < min) min = hit.Score;
            if (hit.Score > max) max = hit.Score;
        }

        var range = max - min;
        foreach (var hit in arm)
            map[hit.Entry.Id] = range > 0 ? (hit.Score - min) / range : 1.0;
        return map;
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
