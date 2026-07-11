using Eidet.Core.Benchmark;
using Eidet.Core.Domain;
using Eidet.Core.Storage;
using Eidet.Core.Text;

namespace Eidet.Core.Maintenance;

/// <summary>
/// Structural, deterministic, zero-LLM merge guard (#39). Before folding <paramref name="discard"/> into
/// <paramref name="survivor"/>, it proves the survivor still surfaces for the discard's OWN retrieval
/// intent: rank the corpus by the discard's content(+tags), take top-k, and require the survivor to be
/// present (<see cref="RetrievalMetrics.RecallAtK"/> ≥ 1). Uses the vector arm when embeddings are
/// configured, and falls back to the same lexical <see cref="WordSimilarity"/> ranking the dedup lexical
/// pass already uses — so NullEnrichment installs never regress. Rejection means the merge is simply not
/// applied (both memories stay live; nothing is forgotten). This is per-merge safety, NOT a substitute
/// for the CI benchmark scorecard's aggregate-recall guarantee.
/// </summary>
public static class RecallConsistencyGuard
{
    // Candidate pool for the lexical fallback — bounded like the dedup sweep.
    private const int PoolCap = 200;

    public static async Task<bool> SurvivesAsync(
        IEidetStore store, string repoId, MemoryEntry survivor, MemoryEntry discard,
        int k = 10, CancellationToken ct = default)
    {
        var intent = discard.Tags.Count > 0
            ? $"{discard.Content} {string.Join(' ', discard.Tags)}"
            : discard.Content;
        var query = new MemoryQuery { Text = intent, Limit = k };

        // Semantic arm first (catches paraphrase near-dups); lexical fallback when embeddings are absent.
        var vector = await store.VectorSearchAsync([repoId], query, ct);
        IReadOnlyList<string> rankedIds;
        if (vector.Count > 0)
        {
            rankedIds = vector.Take(k).Select(e => e.Id).ToList();
        }
        else
        {
            var pool = await store.GetTopScoredAsync(repoId, [discard.Type], PoolCap, ct);
            rankedIds = pool
                .Where(e => e.Validity.IsValidAt(DateTime.UtcNow))
                .OrderByDescending(e => WordSimilarity.Compute(intent, e.Content))
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Take(k)
                .Select(e => e.Id)
                .ToList();
        }

        var gold = new HashSet<string>(new[] { survivor.Id }, StringComparer.OrdinalIgnoreCase);
        return RetrievalMetrics.RecallAtK(rankedIds, gold, k) >= 1.0;
    }
}
