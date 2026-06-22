namespace Eidet.Core.Benchmark;

/// <summary>
/// Pure binary-relevance IR metrics over a ranked id list and a gold set. Every function defines
/// its edge cases out of existence: empty gold yields 0 (a vacuous query scores nothing, never
/// throws or returns NaN), k clamps to the list length, and a missing hit contributes 0 rather
/// than an exception. All results are finite and in <c>[0, 1]</c>.
/// </summary>
public static class RetrievalMetrics
{
    /// <summary>|gold ∩ top-k of <paramref name="ranked"/>| / |gold|. Empty gold → 0. A gold id is
    /// counted at most once even if it appears multiple times in the ranking, so the result can never
    /// exceed 1.0 (the [0,1] contract holds even for a ranking with duplicate ids).</summary>
    public static double RecallAtK(IReadOnlyList<string> ranked, IReadOnlySet<string> gold, int k)
    {
        if (gold.Count == 0) return 0.0;
        var limit = Math.Clamp(k, 0, ranked.Count);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < limit; i++)
            if (gold.Contains(ranked[i])) matched.Add(ranked[i]);
        return (double)matched.Count / gold.Count;
    }

    /// <summary>1 / (1-based rank of the first gold hit), 0 if none appears. Aggregate → MRR.</summary>
    public static double ReciprocalRank(IReadOnlyList<string> ranked, IReadOnlySet<string> gold)
    {
        if (gold.Count == 0) return 0.0;
        for (var i = 0; i < ranked.Count; i++)
            if (gold.Contains(ranked[i])) return 1.0 / (i + 1);
        return 0.0;
    }

    /// <summary>
    /// Standard binary-relevance nDCG@k: DCG over the top-k (gain 1 per gold hit, <c>1/log2(i+2)</c>
    /// discount) divided by the ideal DCG of <c>min(|gold|, k)</c> perfectly-ranked hits. Empty gold
    /// or a zero ideal → 0.
    /// </summary>
    public static double NdcgAtK(IReadOnlyList<string> ranked, IReadOnlySet<string> gold, int k)
    {
        if (gold.Count == 0) return 0.0;
        var limit = Math.Clamp(k, 0, ranked.Count);

        var dcg = 0.0;
        var credited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < limit; i++)
            if (gold.Contains(ranked[i]) && credited.Add(ranked[i]))
                dcg += 1.0 / Math.Log2(i + 2);

        var idealHits = Math.Min(gold.Count, k);
        var idcg = 0.0;
        for (var i = 0; i < idealHits; i++)
            idcg += 1.0 / Math.Log2(i + 2);

        return idcg > 0 ? dcg / idcg : 0.0;
    }
}
