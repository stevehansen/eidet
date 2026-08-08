using Eidet.Core.Canon;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Text;

namespace Eidet.Core.Maintenance;

/// <summary>
/// Dedup engine: folds near-duplicate memories of the same type into a single survivor.
/// Runs a semantic pass (vector recall, catches paraphrases the lexical scan misses) followed
/// by the original lexical Jaccard pass (so embeddings-less installs don't regress), both feeding
/// one deterministic merge routine. Exposed publicly so API / MCP / scheduler can run dedup in
/// dry-run or stand-alone mode without spinning up the full maintenance pipeline.
/// </summary>
public sealed class DedupEngine
{
    private readonly IEidetStore _store;
    private readonly EnrichmentService _enrichment;
    private readonly MemoryService _memory;

    public DedupEngine(IEidetStore store, MemoryService memory, EnrichmentService? enrichment = null)
    {
        _store = store;
        _memory = memory;
        _enrichment = enrichment ?? EnrichmentService.CreateNull();
    }

    public Task<DedupResult> DedupAsync(
        string repoId, bool dryRun = false, CancellationToken ct = default, BulkMutationCtx? write = null) =>
        DedupAsync(repoId, new DedupOptions(), dryRun, ct, write);

    public Task<DedupResult> DedupAsync(
        string repoId, DedupOptions options, bool dryRun = false, CancellationToken ct = default, BulkMutationCtx? write = null) =>
        // Join the caller's bulk scope when handed one (maintenance stage); otherwise open our own
        // so standalone API / MCP / scheduler runs still invalidate the recall cache exactly once.
        write is { } w
            ? DedupCoreAsync(repoId, options, dryRun, w, ct)
            : _memory.RunBulkAsync(w2 => DedupCoreAsync(repoId, options, dryRun, w2, ct),
                                   new BulkOptions { OperationName = "dedup" }, ct);

    private async Task<DedupResult> DedupCoreAsync(
        string repoId, DedupOptions options, bool dryRun, BulkMutationCtx write, CancellationToken ct)
    {
        var result = new DedupResult();
        var types = options.Types ?? Enum.GetValues<MemoryType>();

        foreach (var type in types)
        {
            // A canon:* page is a human-curated memory; dedup must never fold it into (or away for) a
            // machine near-duplicate — exclude it from the candidate set (valence-guard precedent).
            var entries = (await _store.GetTopScoredAsync(repoId, [type], options.CandidatesPerType, ct))
                .Where(e => !CanonTags.IsCanonPage(e))
                .ToList();
            var byId = entries.ToDictionary(e => e.Id);
            var claimed = new HashSet<string>();

            // Semantic pass: catches paraphrased near-duplicates the lexical scan can't see.
            foreach (var entry in entries)
            {
                if (claimed.Contains(entry.Id)) continue;

                var cands = await _store.FindNearDuplicatesAsync(repoId, entry, options.SemanticThreshold, 10, ct);
                foreach (var cand in cands)
                {
                    if (claimed.Contains(entry.Id)) break;   // entry itself was folded away — stop merging into a tombstone
                    if (cand.Id == entry.Id || claimed.Contains(cand.Id)) continue;
                    if (!byId.TryGetValue(cand.Id, out var local)) continue;
                    await MergeAsync(entry, local, claimed, result, dryRun, write, ct);
                }
            }

            // Lexical pass: original O(n²) Jaccard scan, preserved for embeddings-less installs.
            for (var i = 0; i < entries.Count; i++)
            {
                if (claimed.Contains(entries[i].Id)) continue;
                for (var j = i + 1; j < entries.Count; j++)
                {
                    if (claimed.Contains(entries[j].Id)) continue;
                    if (WordSimilarity.Compute(entries[i].Content, entries[j].Content) < LexicalThreshold) continue;
                    await MergeAsync(entries[i], entries[j], claimed, result, dryRun, write, ct);
                }
            }
        }

        return result;
    }

    private async Task MergeAsync(
        MemoryEntry a, MemoryEntry b, HashSet<string> claimed, DedupResult result, bool dryRun, BulkMutationCtx write, CancellationToken ct)
    {
        // Never fold a claim into its contradiction (opposite hard stance).
        if (ValencePolarity.Conflicts(a.Valence, b.Valence)) return;

        var (keep, discard) = a.Importance >= b.Importance ? (a, b) : (b, a);

        // Recall-consistency veto (#39): don't fold the discard away unless the survivor still surfaces
        // for the discard's own retrieval intent — otherwise the merge would silently lose retrievability.
        // A rejected merge forgets nothing (both stay live); the stamp makes it queryable in the dashboard.
        if (!await RecallConsistencyGuard.SurvivesAsync(_store, keep.RepoId, keep, discard, ct: ct))
        {
            result.Rejections.Add(new DedupPair(keep.Id, discard.Id));
            if (!dryRun)
            {
                discard.LastMergeRejectedAt = DateTime.UtcNow;
                await write.WriteAsync(discard, ct);
            }
            return;
        }

        keep.Valence = ValencePolarity.Merge(keep.Valence, discard.Valence);
        keep.AccessCount += discard.AccessCount;
        foreach (var tag in discard.Tags)
        {
            if (!keep.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                keep.Tags.Add(tag);
        }

        discard.Validity.ValidUntil = DateTime.UtcNow;
        discard.ForgetReason = $"Dedup merged into {keep.Id}";

        claimed.Add(discard.Id);
        result.Merges.Add(new DedupPair(keep.Id, discard.Id));

        if (!dryRun)
        {
            await write.WriteAsync(keep, ct);
            await write.WriteAsync(discard, ct);
        }
    }

    private const float LexicalThreshold = 0.85f;
}

public sealed record DedupOptions
{
    /// <summary>
    /// Cosine similarity above which the semantic pass treats two memories of one type as the same claim.
    ///
    /// Measured, not guessed. This pass never actually ran until the vector arm's query was fixed, and the
    /// first dry run against a real corpus showed 0.86 is a *topic* threshold, not a duplicate threshold:
    /// generated insights are all written in the same register, so at 0.86 the pass proposed 682 folds
    /// across two repos with a median word overlap of 0.23 and not one true duplicate. Sweeping upward,
    /// 0.92 gave 490 and 0.95 gave 237 — all still the same false positives. At 0.98 it collapses to 22
    /// proposals with a median overlap of 0.685, nearly all in the 0.50–0.85 band: restatements the
    /// lexical pass genuinely cannot see, which is the whole reason this pass exists.
    ///
    /// Deliberately high. A false negative here costs a duplicate that the next sweep can still catch; a
    /// false positive retires a distinct claim.
    /// </summary>
    public float SemanticThreshold { get; init; } = 0.98f;
    public IReadOnlyList<MemoryType>? Types { get; init; }     // null => all four
    public int CandidatesPerType { get; init; } = 200;
}

public sealed class DedupResult
{
    public List<DedupPair> Merges { get; init; } = [];
    public int MergedCount => Merges.Count;

    /// <summary>Merges the recall-consistency guard vetoed (#39). Nothing was forgotten for these; the
    /// discard stayed live and carries a <c>LastMergeRejectedAt</c> stamp.</summary>
    public List<DedupPair> Rejections { get; init; } = [];
    public int RejectedCount => Rejections.Count;
}

public readonly record struct DedupPair(string KeptId, string DiscardedId);
