using Eidet.Core.Memory;
using Eidet.Core.Storage;

namespace Eidet.Core.Benchmark;

/// <summary>
/// Runs a gold dataset through two rankers on the SAME candidate pools and reports the lift:
/// the v2 hybrid fusion pipeline (<see cref="RecallScoring.Fuse"/> → <see cref="RecallScoring.ApplyTypeBudgets"/>)
/// versus the pre-#33 flat baseline (lexical hit = 1.0, vector-only hit = 0.9, no normalization /
/// UCB / recency → the same budget pass). Hides all metric math behind a single
/// <see cref="Run"/> call: callers pass cases and a fixed <c>now</c>, and get a deterministic
/// <see cref="BenchmarkReport"/>.
/// </summary>
public static class BenchmarkRunner
{
    /// <summary>Flat baseline arm scores (the pre-#33 constants this benchmark proves fusion beats).</summary>
    private const double FlatLexicalScore = 1.0;
    private const double FlatVectorScore = 0.9;

    /// <summary>Per-case metrics under one ranker, tagged with the case's capability and gold size.</summary>
    private readonly record struct CaseResult(
        AmaCapability Capability, double RecallAtK, double Mrr, double NdcgAtK, double GoldSurvival);

    public static BenchmarkReport Run(IReadOnlyList<BenchmarkCase> cases, DateTime now)
    {
        var fused = new List<CaseResult>(cases.Count);
        var baseline = new List<CaseResult>(cases.Count);

        foreach (var c in cases)
        {
            fused.Add(Score(c, RankFused(c, now)));
            baseline.Add(Score(c, RankBaseline(c)));
        }

        return new BenchmarkReport(Aggregate(fused), Aggregate(baseline));
    }

    /// <summary>v2 ranking: real min-max fusion + UCB + recency, then the type-budget truncation.</summary>
    private static (IReadOnlyList<string> Ranked, IReadOnlyList<string> Budgeted) RankFused(
        BenchmarkCase c, DateTime now)
    {
        var weights = RecallWeights.Default with { TotalN = ComputeTotalN(c.Lex, c.Vec) };
        // Fuse once; derive both the full ranking and the post-budget projection from it.
        var fused = RecallScoring.Fuse(c.Lex, c.Vec, weights, now);
        var ranked = fused.Select(f => f.Entry.Id).ToList();
        var results = fused.Select(f => RecallScoring.ToSearchResult(f.Entry, (float)f.Fused)).ToList();
        var budgeted = RecallScoring.ApplyTypeBudgets(results, c.K).Select(r => r.Id).ToList();
        return (ranked, budgeted);
    }

    /// <summary>
    /// Pre-#33 flat ranking, reproduced faithfully: <see cref="RecallInternalAsync"/> merged lexical
    /// hits (in backend relevance order) at a flat 1.0, then vector-only hits (in their order) at a
    /// flat 0.9, and <see cref="RecallScoring.ApplyTypeBudgets"/>'s <c>OrderByDescending(Score)</c> is
    /// a STABLE sort — so that merge order IS the tiebreak. There was no id-based ordering; introducing
    /// one would handicap the baseline beyond its real behavior and inflate the measured lift.
    /// </summary>
    private static (IReadOnlyList<string> Ranked, IReadOnlyList<string> Budgeted) RankBaseline(BenchmarkCase c)
    {
        var lexIds = c.Lex.Select(h => h.Entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = new List<Domain.MemorySearchResult>(c.Lex.Count + c.Vec.Count);
        foreach (var hit in c.Lex)
            merged.Add(RecallScoring.ToSearchResult(hit.Entry, (float)FlatLexicalScore));
        foreach (var hit in c.Vec)
            if (!lexIds.Contains(hit.Entry.Id)) // a lexical hit keeps its 1.0; vector-only takes 0.9
                merged.Add(RecallScoring.ToSearchResult(hit.Entry, (float)FlatVectorScore));

        // merged is already in score-descending, merge-stable order (all 1.0s before all 0.9s),
        // matching what the stable ApplyTypeBudgets sort would produce.
        var ranked = merged.Select(r => r.Id).ToList();
        var budgeted = RecallScoring.ApplyTypeBudgets(merged, c.K).Select(r => r.Id).ToList();
        return (ranked, budgeted);
    }

    private static CaseResult Score(
        BenchmarkCase c, (IReadOnlyList<string> Ranked, IReadOnlyList<string> Budgeted) ids)
    {
        var goldInBudget = ids.Budgeted.Count(id => c.GoldIds.Contains(id));
        var survival = c.GoldIds.Count == 0 ? 0.0 : (double)goldInBudget / c.GoldIds.Count;

        return new CaseResult(
            c.Capability,
            RetrievalMetrics.RecallAtK(ids.Ranked, c.GoldIds, c.K),
            RetrievalMetrics.ReciprocalRank(ids.Ranked, c.GoldIds),
            RetrievalMetrics.NdcgAtK(ids.Ranked, c.GoldIds, c.K),
            survival);
    }

    private static List<CapabilityScore> Aggregate(IReadOnlyList<CaseResult> results) =>
        results
            .GroupBy(r => r.Capability)
            .OrderBy(g => g.Key)
            .Select(g => new CapabilityScore(
                g.Key,
                g.Count(),
                g.Average(r => r.RecallAtK),
                g.Average(r => r.Mrr),
                g.Average(r => r.NdcgAtK),
                g.Average(r => r.GoldSurvival)))
            .ToList();

    /// <summary>Σ (Echo+Fizzle) over lex∪vec (dedup by id) — the UCB exploration denominator base.</summary>
    private static long ComputeTotalN(IReadOnlyList<ScoredHit> lex, IReadOnlyList<ScoredHit> vec)
    {
        var seen = new Dictionary<string, Domain.MemoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in lex) seen.TryAdd(hit.Entry.Id, hit.Entry);
        foreach (var hit in vec) seen.TryAdd(hit.Entry.Id, hit.Entry);
        return seen.Values.Sum(e => (long)e.EchoCount + e.FizzleCount);
    }
}
