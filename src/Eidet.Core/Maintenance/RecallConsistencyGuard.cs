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
    // The ranking page both arms ask for. Wider than k on purpose — see the fetch comment below.
    private const int PoolCap = 200;

    public static async Task<bool> SurvivesAsync(
        IEidetStore store, string repoId, MemoryEntry survivor, MemoryEntry discard,
        int k = 10, CancellationToken ct = default)
    {
        var intent = discard.Tags.Count > 0
            ? $"{discard.Content} {string.Join(' ', discard.Tags)}"
            : discard.Content;
        // Rows already retired are dropped AFTER the fetch, so a page sized at k alone can come back
        // holding nothing but rows that are then discarded — and an absent survivor reads exactly like
        // one that doesn't surface, which vetoes the fold. How many such rows there are isn't knowable
        // up front (a bulk run retires them as it goes), so the page is simply wide enough that the
        // survivor is on it. The lexical arm always had this width; the vector arm was sized from k
        // and starved.
        var query = new MemoryQuery { Text = intent, Limit = PoolCap };

        // Semantic arm first (catches paraphrase near-dups); lexical fallback when embeddings are absent.
        var vector = await store.VectorSearchAsync([repoId], query, ct);
        IReadOnlyList<string> rankedIds;
        if (vector.Count > 0)
        {
            rankedIds = Staying(vector).Take(k).Select(e => e.Id).ToList();
        }
        else
        {
            // Query-aware pool: full-text hits for the intent, not top-by-importance — a scored
            // pool could exclude the survivor entirely and falsely veto every merge in big repos.
            var lexicalQuery = new MemoryQuery { Text = intent, Type = discard.Type, Limit = PoolCap };
            var pool = await store.FullTextSearchAsync([repoId], lexicalQuery, ct);
            rankedIds = Staying(pool)
                .OrderByDescending(e => WordSimilarity.Compute(intent, e.Content))
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Take(k)
                .Select(e => e.Id)
                .ToList();
        }

        var gold = new HashSet<string>(new[] { survivor.Id }, StringComparer.OrdinalIgnoreCase);
        return RetrievalMetrics.RecallAtK(rankedIds, gold, k) >= 1.0;
    }

    /// <summary>
    /// The ranked rows that are actually still in the corpus — anything already retired dropped.
    ///
    /// This is not redundant with the store's own live-only filter. That filter is applied by the search
    /// index, and a bulk run commits each retirement to the document straight away while the index
    /// catches up afterwards — so a ranking taken mid-run still lists rows whose document is already
    /// closed. The documents themselves are current, so re-reading validity here is what makes the guard
    /// immune to that lag. Both arms need it: memories folded a moment ago are exactly the crowd most
    /// likely to dominate the ranking for the next merge's intent, and counting them would veto every
    /// fold after the first.
    /// </summary>
    private static IEnumerable<MemoryEntry> Staying(IEnumerable<MemoryEntry> ranked)
    {
        var now = DateTime.UtcNow;
        return ranked.Where(e => e.Validity.IsValidAt(now));
    }
}
